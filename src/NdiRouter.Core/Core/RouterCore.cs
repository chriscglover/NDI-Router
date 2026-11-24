using NdiRouter.Ndi;

namespace NdiRouter.Core;

public sealed class RouteChangedEventArgs(int output, int? previousInput, int? input, string origin, int? level)
    : EventArgs
{
    public int Output { get; } = output;
    public int? PreviousInput { get; } = previousInput;

    /// <summary>The input now routed, or null when the output was cleared.</summary>
    public int? Input { get; } = input;

    /// <summary>Who asked: "panel", "SW-P-08 10.0.0.5:51234", "preset 3"...</summary>
    public string Origin { get; } = origin;

    /// <summary>The SW-P-08 level the request arrived on, when it came from a controller.</summary>
    public int? Level { get; } = level;

    public bool Changed => PreviousInput != Input;
}

public sealed class NamesChangedEventArgs(bool inputs, bool outputs) : EventArgs
{
    public bool Inputs { get; } = inputs;
    public bool Outputs { get; } = outputs;
}

public sealed class LockChangedEventArgs(int output, OutputLock? previous, OutputLock? current, string origin) : EventArgs
{
    public int Output { get; } = output;
    public OutputLock? Previous { get; } = previous;

    /// <summary>The lock now on the output, or null when it is free.</summary>
    public OutputLock? Current { get; } = current;

    public string Origin { get; } = origin;

    public bool Changed => Previous != Current;
}

/// <summary>
/// The router: inputs, outputs, presets and the crosspoint state, plus the NDI routing
/// instance behind each output.
///
/// Every method is safe to call from any thread - the panel, SW-P-08 connections and the
/// TSL sender all use it at once. Events are raised on the calling thread, after the lock is
/// released, so a handler can read the router back without deadlocking.
/// </summary>
public sealed class RouterCore : IDisposable
{
    public const int MaxInputs = 1000;
    public const int MaxOutputs = 256;
    public const int MaxPresets = 64;

    private readonly object _gate = new();
    private readonly INdiOutputFactory _factory;
    private readonly List<RouterInput> _inputs = new();
    private readonly List<RouterOutput> _outputs = new();
    private readonly List<INdiOutput> _ndiOutputs = new();
    private readonly List<RouterPreset> _presets = new();
    private string _outputGroups;
    private bool _disposed;

    public RouterCore(
        INdiOutputFactory factory,
        IEnumerable<RouterInput> inputs,
        IEnumerable<RouterOutput> outputs,
        IEnumerable<RouterPreset> presets,
        string outputGroups = "")
    {
        _factory = factory;
        _outputGroups = outputGroups ?? string.Empty;

        _inputs.AddRange(inputs.Take(MaxInputs).Select(i => i.Clone()));
        _outputs.AddRange(outputs.Take(MaxOutputs).Select(o => o.Clone()));
        _presets.AddRange(presets.Take(MaxPresets).Select(p => p.Clone()));

        foreach (var output in _outputs)
        {
            // A saved route to an input that no longer exists is dropped rather than kept dangling.
            if (output.Input is int input && (input < 0 || input >= _inputs.Count)) output.Input = null;
        }

        for (var i = 0; i < _outputs.Count; i++)
        {
            _ndiOutputs.Add(CreateNdiOutput(i));
            ApplyToNdi(i);
        }
    }

    /// <summary>A crosspoint was made or cleared - also raised when a route is re-made unchanged,
    /// because SW-P-08 controllers expect every Connect to be confirmed.</summary>
    public event EventHandler<RouteChangedEventArgs>? RouteChanged;

    /// <summary>An input's or output's label, short name or NDI assignment changed.</summary>
    public event EventHandler<NamesChangedEventArgs>? NamesChanged;

    /// <summary>The number of inputs, outputs or presets changed, or a preset was edited.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>An output was locked or unlocked - also raised when a lock is re-made unchanged,
    /// because SW-P-08 controllers expect every PROTECT CONNECT to be confirmed.</summary>
    public event EventHandler<LockChangedEventArgs>? LockChanged;

    /// <summary>Something worth telling the operator.</summary>
    public event Action<string>? Log;

    // ------------------------------------------------------------------ reading

    public int InputCount { get { lock (_gate) return _inputs.Count; } }
    public int OutputCount { get { lock (_gate) return _outputs.Count; } }
    public int PresetCount { get { lock (_gate) return _presets.Count; } }

    public RouterInput GetInput(int index) { lock (_gate) return _inputs[index].Clone(); }
    public RouterOutput GetOutput(int index) { lock (_gate) return _outputs[index].Clone(); }
    public RouterPreset GetPreset(int index) { lock (_gate) return _presets[index].Clone(); }

    public List<RouterInput> Inputs() { lock (_gate) return _inputs.Select(i => i.Clone()).ToList(); }
    public List<RouterOutput> Outputs() { lock (_gate) return _outputs.Select(o => o.Clone()).ToList(); }
    public List<RouterPreset> Presets() { lock (_gate) return _presets.Select(p => p.Clone()).ToList(); }

    public int? GetRoute(int output)
    {
        lock (_gate) return output >= 0 && output < _outputs.Count ? _outputs[output].Input : null;
    }

    /// <summary>Every output's route, by output index.</summary>
    public int?[] Routes()
    {
        lock (_gate) return _outputs.Select(o => o.Input).ToArray();
    }

    /// <summary>The name a control system is sent for an input, at the length it asked for.</summary>
    public string InputName(int index, int length)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _inputs.Count) return string.Empty;
            return Swp08Names.For(_inputs[index], length, $"IN {index + 1}");
        }
    }

    /// <summary>The name a control system is sent for an output, at the length it asked for.</summary>
    public string OutputName(int index, int length)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _outputs.Count) return string.Empty;
            return Swp08Names.For(_outputs[index], length, $"OUT {index + 1}");
        }
    }

    private static bool NamesDiffer(ISwp08Named a, ISwp08Named b) =>
        a.Label != b.Label || a.Name4 != b.Name4 || a.Name8 != b.Name8 || a.Name12 != b.Name12;

    /// <summary>NDI receivers currently watching an output.</summary>
    public int Connections(int output)
    {
        lock (_gate)
        {
            if (output < 0 || output >= _ndiOutputs.Count) return 0;
            try { return _ndiOutputs[output].Connections(); }
            catch { return 0; }
        }
    }

    // ------------------------------------------------------------------ routing

    /// <summary>
    /// Routes an input to an output, or clears the output when <paramref name="input"/> is null.
    /// Returns false when the output or input does not exist, or the output is locked by someone
    /// other than <paramref name="requester"/>. A null requester is the router itself - an import
    /// or a resize - which locks do not stop.
    /// </summary>
    public bool Route(int output, int? input, string origin, int? level = null, string? requester = null)
    {
        RouteChangedEventArgs? args = null;
        string? refused = null;
        lock (_gate)
        {
            if (output < 0 || output >= _outputs.Count) return false;
            if (input is int i && (i < 0 || i >= _inputs.Count)) return false;

            if (LockedAgainst(output, requester) is { } held)
            {
                refused = $"Output {output + 1} \"{_outputs[output].Label}\" is locked by {held}; route from {origin} refused.";
            }
            else
            {
                var previous = _outputs[output].Input;
                _outputs[output].Input = input;
                if (previous != input) ApplyToNdi(output);
                args = new RouteChangedEventArgs(output, previous, input, origin, level);
            }
        }

        if (args is null)
        {
            Log?.Invoke(refused!);
            return false;
        }

        RouteChanged?.Invoke(this, args);
        return true;
    }

    /// <summary>Routes one input to several outputs. Returns the outputs that refused because they are locked.</summary>
    public List<int> Route(IEnumerable<int> outputs, int? input, string origin, string? requester = null)
    {
        var refused = new List<int>();
        foreach (var output in outputs.Distinct().ToList())
            if (!Route(output, input, origin, requester: requester) && IsLockedAgainst(output, requester))
                refused.Add(output);
        return refused;
    }

    // ------------------------------------------------------------------ locks

    public OutputLock? GetLock(int output)
    {
        lock (_gate) return output >= 0 && output < _outputs.Count ? _outputs[output].Lock : null;
    }

    /// <summary>Every output's lock, by output index.</summary>
    public OutputLock?[] Locks()
    {
        lock (_gate) return _outputs.Select(o => o.Lock).ToArray();
    }

    /// <summary>Whether a route from <paramref name="requester"/> would be refused because someone else holds the output.</summary>
    public bool IsLockedAgainst(int output, string? requester)
    {
        lock (_gate) return output >= 0 && output < _outputs.Count && LockedAgainst(output, requester) is not null;
    }

    private OutputLock? LockedAgainst(int output, string? requester) =>
        requester is not null && _outputs[output].Lock is { } held && held.Owner != requester ? held : null;

    /// <summary>
    /// Locks an output for <paramref name="owner"/>. Re-locking one it already holds succeeds (and
    /// takes the new device number); one held by someone else is refused.
    /// </summary>
    public bool Lock(int output, OutputLock owner, string origin)
    {
        LockChangedEventArgs args;
        lock (_gate)
        {
            if (output < 0 || output >= _outputs.Count) return false;
            var previous = _outputs[output].Lock;
            if (previous is not null && previous.Owner != owner.Owner) return false;

            _outputs[output].Lock = owner;
            args = new LockChangedEventArgs(output, previous, owner, origin);
        }

        LockChanged?.Invoke(this, args);
        return true;
    }

    /// <summary>
    /// Unlocks an output. Only its owner can, unless <paramref name="force"/> - Videohub's force
    /// unlock, and the panel, which is in charge of the router. Unlocking a free output succeeds.
    /// </summary>
    public bool Unlock(int output, string owner, string origin, bool force = false)
    {
        LockChangedEventArgs args;
        lock (_gate)
        {
            if (output < 0 || output >= _outputs.Count) return false;
            var previous = _outputs[output].Lock;
            if (previous is not null && previous.Owner != owner && !force) return false;

            _outputs[output].Lock = null;
            args = new LockChangedEventArgs(output, previous, null, origin);
        }

        LockChanged?.Invoke(this, args);
        return true;
    }

    /// <summary>Points the output's NDI routing instance at its input's source.</summary>
    private void ApplyToNdi(int output)
    {
        var ndi = _ndiOutputs[output];
        var route = _outputs[output].Input;
        var source = route is int i && i < _inputs.Count ? _inputs[i].NdiSource : string.Empty;

        try
        {
            var ok = string.IsNullOrWhiteSpace(source) ? ndi.Clear() : ndi.Route(source);
            if (!ok) Log?.Invoke($"NDI did not accept the route for {_outputs[output].NdiName}.");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"NDI routing failed for {_outputs[output].NdiName}: {ex.Message}");
        }
    }

    private INdiOutput CreateNdiOutput(int output)
    {
        var name = _outputs[output].NdiName;
        if (string.IsNullOrWhiteSpace(name)) name = $"Router Out {output + 1}";

        try
        {
            return _factory.CreateOutput(name, _outputGroups);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not publish NDI output \"{name}\": {ex.Message}");
            return new OfflineOutput(name);
        }
    }

    // ------------------------------------------------------------------ editing

    public void UpdateInput(int index, Action<RouterInput> edit)
    {
        NamesChangedEventArgs? names = null;
        lock (_gate)
        {
            if (index < 0 || index >= _inputs.Count) return;
            var input = _inputs[index];
            var before = input.Clone();
            edit(input);

            if (!string.Equals(before.NdiSource, input.NdiSource, StringComparison.Ordinal))
            {
                // Re-patching an input changes what every output on it carries, the way
                // re-plugging a router's input port would.
                for (var o = 0; o < _outputs.Count; o++)
                    if (_outputs[o].Input == index) ApplyToNdi(o);
            }

            if (NamesDiffer(before, input) || before.NdiSource != input.NdiSource)
                names = new NamesChangedEventArgs(inputs: true, outputs: false);
        }

        if (names is not null) NamesChanged?.Invoke(this, names);
    }

    /// <summary>
    /// Assigns an NDI source to an input, which takes the source's name - the part in brackets - as
    /// its label, whether it was picked or typed. The label can be changed afterwards; it is only
    /// replaced again when the source changes, or is unassigned, which puts back "Input n".
    /// </summary>
    public void AssignSource(int index, string source)
    {
        source = (source ?? string.Empty).Trim();
        UpdateInput(index, input =>
        {
            if (input.NdiSource == source) return; // the same source again keeps a typed label
            input.NdiSource = source;
            input.Label = source.Length > 0 ? NdiName.Channel(source) : RouterDefaults.Input(index).Label;
        });
    }

    public void UpdateOutput(int index, Action<RouterOutput> edit)
    {
        NamesChangedEventArgs? names = null;
        lock (_gate)
        {
            if (index < 0 || index >= _outputs.Count) return;
            var output = _outputs[index];
            var before = output.Clone();
            edit(output);
            output.Input = before.Input; // routes and locks only change through Route() and Lock(),
            output.Lock = before.Lock;   // so they are always announced

            if (!string.Equals(before.NdiName, output.NdiName, StringComparison.Ordinal))
            {
                _ndiOutputs[index].Dispose();
                _ndiOutputs[index] = CreateNdiOutput(index);
                ApplyToNdi(index);
            }

            if (NamesDiffer(before, output) || before.NdiName != output.NdiName ||
                before.UmdAddress != output.UmdAddress || before.UmdEnabled != output.UmdEnabled)
                names = new NamesChangedEventArgs(inputs: false, outputs: true);
        }

        if (names is not null) NamesChanged?.Invoke(this, names);
    }

    /// <summary>Republishes every output in a new set of NDI groups.</summary>
    public void SetOutputGroups(string groups)
    {
        lock (_gate)
        {
            groups ??= string.Empty;
            if (groups == _outputGroups) return;
            _outputGroups = groups;

            for (var i = 0; i < _ndiOutputs.Count; i++)
            {
                _ndiOutputs[i].Dispose();
                _ndiOutputs[i] = CreateNdiOutput(i);
                ApplyToNdi(i);
            }
        }
    }

    /// <summary>Grows or shrinks the router. Outputs routed to an input that goes away are cleared.</summary>
    public void Resize(int inputs, int outputs, int presets)
    {
        inputs = Math.Clamp(inputs, 1, MaxInputs);
        outputs = Math.Clamp(outputs, 1, MaxOutputs);
        presets = Math.Clamp(presets, 0, MaxPresets);

        var cleared = new List<RouteChangedEventArgs>();
        lock (_gate)
        {
            while (_inputs.Count < inputs) _inputs.Add(RouterDefaults.Input(_inputs.Count));
            if (_inputs.Count > inputs) _inputs.RemoveRange(inputs, _inputs.Count - inputs);

            while (_outputs.Count < outputs)
            {
                var index = _outputs.Count;
                var output = RouterDefaults.Output(index);
                // Never publish two outputs under one name.
                while (_outputs.Any(o => o.NdiName == output.NdiName)) output.NdiName += "+";
                _outputs.Add(output);
                _ndiOutputs.Add(CreateNdiOutput(index));
                ApplyToNdi(index);
            }

            if (_outputs.Count > outputs)
            {
                for (var i = outputs; i < _ndiOutputs.Count; i++) _ndiOutputs[i].Dispose();
                _ndiOutputs.RemoveRange(outputs, _ndiOutputs.Count - outputs);
                _outputs.RemoveRange(outputs, _outputs.Count - outputs);
            }

            for (var o = 0; o < _outputs.Count; o++)
            {
                if (_outputs[o].Input is int i && i >= _inputs.Count)
                {
                    _outputs[o].Input = null;
                    ApplyToNdi(o);
                    cleared.Add(new RouteChangedEventArgs(o, i, null, "resize", null));
                }
            }

            while (_presets.Count < presets) _presets.Add(RouterDefaults.Preset(_presets.Count));
            if (_presets.Count > presets) _presets.RemoveRange(presets, _presets.Count - presets);
        }

        foreach (var args in cleared) RouteChanged?.Invoke(this, args);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        NamesChanged?.Invoke(this, new NamesChangedEventArgs(true, true));
    }

    // ------------------------------------------------------------------ presets

    public void StorePreset(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _presets.Count) return;
            _presets[index].Routes = _outputs.Select(o => o.Input).ToList();
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearPreset(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _presets.Count) return;
            _presets[index].Routes = null;
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenamePreset(int index, string label)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _presets.Count) return;
            _presets[index].Label = label;
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies a stored preset. Outputs added since it was stored are left alone, and so are
    /// outputs locked by someone other than <paramref name="requester"/>.
    /// </summary>
    public bool RecallPreset(int index, string? requester = null)
    {
        List<int?> routes;
        string label;
        lock (_gate)
        {
            if (index < 0 || index >= _presets.Count || _presets[index].Routes is null) return false;
            routes = new List<int?>(_presets[index].Routes!);
            label = _presets[index].Label;
        }

        var origin = $"preset \"{label}\"";
        for (var o = 0; o < routes.Count && o < OutputCount; o++)
        {
            var input = routes[o];
            if (input is int i && i >= InputCount) input = null;
            Route(o, input, origin, requester: requester);
        }

        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var output in _ndiOutputs) output.Dispose();
            _ndiOutputs.Clear();
        }
    }
}
