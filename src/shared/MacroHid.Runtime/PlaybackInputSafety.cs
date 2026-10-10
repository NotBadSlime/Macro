using MacroHid.Core;

namespace MacroHid.Runtime;

internal sealed record PlaybackInputSafetyPlan(
    IReadOnlyList<InputAction> EmergencyReleaseActions,
    IReadOnlyList<InputAction> IterationBoundaryReleaseActions)
{
    public static PlaybackInputSafetyPlan Create(
        MacroDocument document,
        Func<string, MacroDocument?>? macroResolver)
    {
        var emergency = new ReleaseAccumulator();
        var visiting = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance);
        CollectEmergency(document, macroResolver, emergency, visiting, depth: 0);

        var boundary = new HeldInputState();
        visiting.Clear();
        ApplyDocument(document, macroResolver, boundary, visiting, depth: 0);
        return new PlaybackInputSafetyPlan(emergency.ToActions(), boundary.ToReleaseActions());
    }

    private static void CollectEmergency(
        MacroDocument document,
        Func<string, MacroDocument?>? macroResolver,
        ReleaseAccumulator releases,
        HashSet<MacroDocument> visiting,
        int depth)
    {
        if (depth >= 32 || !visiting.Add(document))
        {
            return;
        }

        try
        {
            CollectEmergencySteps(document.Steps, macroResolver, releases, visiting, depth);
            foreach (var condition in document.EffectiveConditions)
            {
                CollectEmergencySteps(condition.ThenSteps, macroResolver, releases, visiting, depth);
            }
        }
        finally
        {
            visiting.Remove(document);
        }
    }

    private static void CollectEmergencySteps(
        IReadOnlyList<MacroStep> steps,
        Func<string, MacroDocument?>? macroResolver,
        ReleaseAccumulator releases,
        HashSet<MacroDocument> visiting,
        int depth)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case KeyStep { Kind: KeyActionKind.Down or KeyActionKind.Tap } key:
                    releases.AddKey(key.Key, key.Modifiers);
                    break;
                case TextStep:
                    // Text injection can use the clipboard Ctrl+V fast path.
                    releases.AddKey(HidKey.V, HidModifier.LeftCtrl);
                    break;
                case MouseButtonStep { Kind: ButtonActionKind.Down or ButtonActionKind.Click } button:
                    releases.AddMouse(button.Button);
                    break;
                case OcrClickStep ocrClick:
                    releases.AddMouse(ocrClick.Button);
                    break;
                case ConsumerStep { Kind: ButtonActionKind.Down or ButtonActionKind.Click } consumer:
                    releases.AddConsumer(consumer.Control);
                    break;
                case RepeatStep repeat:
                    CollectEmergencySteps(repeat.Steps, macroResolver, releases, visiting, depth);
                    break;
                case PixelWhenStep pixel:
                    CollectEmergencySteps(pixel.ThenSteps, macroResolver, releases, visiting, depth);
                    break;
                case TextWhenStep text:
                    CollectEmergencySteps(text.ThenSteps, macroResolver, releases, visiting, depth);
                    break;
                case ParallelStep parallel:
                    CollectEmergencySteps(parallel.Left.Steps, macroResolver, releases, visiting, depth);
                    CollectEmergencySteps(parallel.Right.Steps, macroResolver, releases, visiting, depth);
                    break;
                case ParallelBranchStep branch:
                    CollectEmergencySteps(branch.Steps, macroResolver, releases, visiting, depth);
                    break;
                case MacroCallStep macro when macroResolver?.Invoke(macro.Macro) is { } nested:
                    CollectEmergency(nested, macroResolver, releases, visiting, depth + 1);
                    break;
            }
        }
    }

    private static void ApplyDocument(
        MacroDocument document,
        Func<string, MacroDocument?>? macroResolver,
        HeldInputState state,
        HashSet<MacroDocument> visiting,
        int depth)
    {
        if (depth >= 32 || !visiting.Add(document))
        {
            return;
        }

        try
        {
            ApplySteps(document.Steps, macroResolver, state, visiting, depth);
            foreach (var condition in document.EffectiveConditions)
            {
                var possibleConditionState = state.Clone();
                ApplySteps(condition.ThenSteps, macroResolver, possibleConditionState, visiting, depth);
                state.UnionWith(possibleConditionState);
            }
        }
        finally
        {
            visiting.Remove(document);
        }
    }

    private static bool ApplySteps(
        IReadOnlyList<MacroStep> steps,
        Func<string, MacroDocument?>? macroResolver,
        HeldInputState state,
        HashSet<MacroDocument> visiting,
        int depth)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case KeyStep { Kind: KeyActionKind.Down } key:
                    state.PressKey(key.Key, key.Modifiers);
                    break;
                case KeyStep { Kind: KeyActionKind.Up } key:
                    state.ReleaseKey(key.Key, key.Modifiers);
                    break;
                case MouseButtonStep { Kind: ButtonActionKind.Down } button:
                    state.PressMouse(button.Button);
                    break;
                case MouseButtonStep { Kind: ButtonActionKind.Up } button:
                    state.ReleaseMouse(button.Button);
                    break;
                case ConsumerStep { Kind: ButtonActionKind.Down } consumer:
                    state.PressConsumer(consumer.Control);
                    break;
                case ConsumerStep { Kind: ButtonActionKind.Up } consumer:
                    state.ReleaseConsumer(consumer.Control);
                    break;
                case RepeatStep { Count: > 0 } repeat:
                    // Down/up state transitions become stable after one pass.
                    if (ApplySteps(repeat.Steps, macroResolver, state, visiting, depth))
                    {
                        return true;
                    }
                    break;
                case PixelWhenStep pixel:
                    UnionOptionalBranch(pixel.ThenSteps, macroResolver, state, visiting, depth);
                    break;
                case TextWhenStep text:
                    UnionOptionalBranch(text.ThenSteps, macroResolver, state, visiting, depth);
                    break;
                case ParallelStep parallel:
                    var left = state.Clone();
                    var right = state.Clone();
                    ApplySteps(parallel.Left.Steps, macroResolver, left, visiting, depth);
                    ApplySteps(parallel.Right.Steps, macroResolver, right, visiting, depth);
                    state.UnionWith(left);
                    state.UnionWith(right);
                    break;
                case ParallelBranchStep branch:
                    if (ApplySteps(branch.Steps, macroResolver, state, visiting, depth))
                    {
                        return true;
                    }
                    break;
                case MacroCallStep macro when macroResolver?.Invoke(macro.Macro) is { } nested:
                    ApplyDocument(nested, macroResolver, state, visiting, depth + 1);
                    break;
                case StopCurrentSequenceStep or StopCurrentIterationStep or StopAllSequencesStep:
                    return true;
            }
        }

        return false;
    }

    private static void UnionOptionalBranch(
        IReadOnlyList<MacroStep> steps,
        Func<string, MacroDocument?>? macroResolver,
        HeldInputState state,
        HashSet<MacroDocument> visiting,
        int depth)
    {
        var branch = state.Clone();
        ApplySteps(steps, macroResolver, branch, visiting, depth);
        state.UnionWith(branch);
    }

    private sealed class ReleaseAccumulator
    {
        private readonly Dictionary<HidKey, HidModifier> keys = [];
        private readonly HashSet<MouseButton> mouseButtons = [];
        private readonly HashSet<ConsumerControl> consumerControls = [];
        private HidModifier modifiers;

        public void AddKey(HidKey key, HidModifier keyModifiers)
        {
            if (key != HidKey.None)
            {
                keys[key] = keys.GetValueOrDefault(key) | keyModifiers;
            }
            modifiers |= keyModifiers;
        }

        public void AddMouse(MouseButton buttons)
        {
            foreach (var button in StandardMouseButtons(buttons))
            {
                mouseButtons.Add(button);
            }
        }

        public void AddConsumer(ConsumerControl control) => consumerControls.Add(control);

        public IReadOnlyList<InputAction> ToActions()
        {
            var actions = new List<InputAction>();
            actions.AddRange(keys.OrderBy(pair => pair.Key)
                .Select(pair => (InputAction)new KeyInputAction(KeyActionKind.Up, pair.Key, pair.Value)));
            actions.AddRange(mouseButtons.OrderBy(button => button)
                .Select(button => (InputAction)new MouseButtonInputAction(button, ButtonActionKind.Up)));
            actions.AddRange(consumerControls.OrderBy(control => control)
                .Select(control => (InputAction)new ConsumerInputAction(control, ButtonActionKind.Up)));
            if (modifiers != HidModifier.None)
            {
                actions.Add(new KeyInputAction(KeyActionKind.Up, HidKey.None, modifiers));
            }
            return actions;
        }
    }

    private sealed class HeldInputState
    {
        private readonly Dictionary<HidKey, HidModifier> keys = [];
        private readonly HashSet<MouseButton> mouseButtons = [];
        private readonly HashSet<ConsumerControl> consumerControls = [];
        private HidModifier modifiers;

        public void PressKey(HidKey key, HidModifier keyModifiers)
        {
            if (key != HidKey.None)
            {
                keys[key] = keys.GetValueOrDefault(key) | keyModifiers;
            }
            modifiers |= keyModifiers;
        }

        public void ReleaseKey(HidKey key, HidModifier keyModifiers)
        {
            if (key != HidKey.None)
            {
                keys.Remove(key);
            }
            modifiers &= ~keyModifiers;
        }

        public void PressMouse(MouseButton buttons)
        {
            foreach (var button in StandardMouseButtons(buttons)) mouseButtons.Add(button);
        }

        public void ReleaseMouse(MouseButton buttons)
        {
            foreach (var button in StandardMouseButtons(buttons)) mouseButtons.Remove(button);
        }

        public void PressConsumer(ConsumerControl control) => consumerControls.Add(control);
        public void ReleaseConsumer(ConsumerControl control) => consumerControls.Remove(control);

        public HeldInputState Clone()
        {
            var clone = new HeldInputState { modifiers = modifiers };
            foreach (var pair in keys) clone.keys[pair.Key] = pair.Value;
            clone.mouseButtons.UnionWith(mouseButtons);
            clone.consumerControls.UnionWith(consumerControls);
            return clone;
        }

        public void UnionWith(HeldInputState other)
        {
            foreach (var pair in other.keys)
            {
                keys[pair.Key] = keys.GetValueOrDefault(pair.Key) | pair.Value;
            }
            mouseButtons.UnionWith(other.mouseButtons);
            consumerControls.UnionWith(other.consumerControls);
            modifiers |= other.modifiers;
        }

        public IReadOnlyList<InputAction> ToReleaseActions()
        {
            var releases = new ReleaseAccumulator();
            foreach (var pair in keys) releases.AddKey(pair.Key, pair.Value);
            foreach (var button in mouseButtons) releases.AddMouse(button);
            foreach (var control in consumerControls) releases.AddConsumer(control);
            if (modifiers != HidModifier.None) releases.AddKey(HidKey.None, modifiers);
            return releases.ToActions();
        }
    }

    private static IEnumerable<MouseButton> StandardMouseButtons(MouseButton buttons)
    {
        foreach (var button in new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.X1, MouseButton.X2 })
        {
            if ((buttons & button) != 0) yield return button;
        }
    }
}

public static class PlaybackInputSafetyRegistry
{
    private sealed record ActivePlayback(IMacroInputSink Sink, IReadOnlyList<InputAction> Releases);

    private static readonly object Gate = new();
    private static readonly Dictionary<long, ActivePlayback> Active = [];
    private static long nextId;

    internal static IDisposable Register(IMacroInputSink sink, IReadOnlyList<InputAction> releases)
    {
        var id = Interlocked.Increment(ref nextId);
        lock (Gate)
        {
            Active[id] = new ActivePlayback(sink, releases);
        }
        return new Registration(id);
    }

    public static void ReleaseAllActiveInputs()
    {
        ActivePlayback[] snapshot;
        lock (Gate)
        {
            snapshot = Active.Values.ToArray();
        }

        foreach (var playback in snapshot)
        {
            SubmitBestEffort(playback.Sink, playback.Releases);
        }
    }

    internal static int SubmitBestEffort(IMacroInputSink sink, IReadOnlyList<InputAction> releases)
    {
        var submitted = 0;
        var sequence = uint.MaxValue - (uint)Math.Min(releases.Count, 1024);
        foreach (var release in releases)
        {
            try
            {
                sink.Submit(sequence++, release);
                submitted++;
            }
            catch
            {
                // Cleanup must never replace the original playback failure.
            }
        }
        return submitted;
    }

    private sealed class Registration(long id) : IDisposable
    {
        private long registrationId = id;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref registrationId, 0);
            if (current == 0) return;
            lock (Gate)
            {
                Active.Remove(current);
            }
        }
    }
}
