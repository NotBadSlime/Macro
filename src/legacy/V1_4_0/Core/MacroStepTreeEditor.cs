namespace MacroHid.Core;

public static class MacroStepTreeEditor
{
    public static MacroStep GetAtPath(IReadOnlyList<MacroStep> steps, IReadOnlyList<int> path)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("Step path cannot be empty.", nameof(path));
        }

        var currentSteps = steps;
        MacroStep current = currentSteps[CheckedIndex(path[0], currentSteps.Count)];
        for (var i = 1; i < path.Count; i++)
        {
            currentSteps = current switch
            {
                RepeatStep repeat => repeat.Steps,
                PixelWhenStep pixel => pixel.ThenSteps,
                _ => throw new InvalidOperationException($"Step '{current.GetType().Name}' cannot contain child steps.")
            };
            current = currentSteps[CheckedIndex(path[i], currentSteps.Count)];
        }

        return current;
    }

    public static IReadOnlyList<MacroStep> InsertAtPath(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<int> parentPath,
        int insertIndex,
        IReadOnlyList<MacroStep> stepsToInsert)
    {
        var normalized = MacroStepNormalizer.NormalizeSteps(stepsToInsert);
        if (normalized.Count == 0)
        {
            return steps.ToList();
        }

        if (parentPath.Count == 0)
        {
            var result = steps.ToList();
            result.InsertRange(Math.Clamp(insertIndex, 0, result.Count), normalized);
            return result;
        }

        var parent = GetAtPath(steps, parentPath);
        MacroStep updatedParent = parent switch
        {
            RepeatStep repeat => repeat with { Steps = InsertAtPath(repeat.Steps, [], insertIndex, normalized) },
            PixelWhenStep pixel => pixel with { ThenSteps = InsertAtPath(pixel.ThenSteps, [], insertIndex, normalized) },
            _ => throw new InvalidOperationException($"Step '{parent.GetType().Name}' cannot contain child steps.")
        };

        return ReplaceAtPath(steps, parentPath, updatedParent);
    }

    public static IReadOnlyList<MacroStep> DeleteAtPath(IReadOnlyList<MacroStep> steps, IReadOnlyList<int> path)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("Step path cannot be empty.", nameof(path));
        }

        if (path.Count == 1)
        {
            var result = steps.ToList();
            result.RemoveAt(CheckedIndex(path[0], result.Count));
            return result;
        }

        var parentPath = path.Take(path.Count - 1).ToArray();
        var childIndex = path[^1];
        var parent = GetAtPath(steps, parentPath);
        MacroStep updatedParent = parent switch
        {
            RepeatStep repeat => repeat with { Steps = DeleteAtPath(repeat.Steps, [childIndex]) },
            PixelWhenStep pixel => pixel with { ThenSteps = DeleteAtPath(pixel.ThenSteps, [childIndex]) },
            _ => throw new InvalidOperationException($"Step '{parent.GetType().Name}' cannot contain child steps.")
        };

        return ReplaceAtPath(steps, parentPath, updatedParent);
    }

    public static IReadOnlyList<MacroStep> ReplaceAtPath(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<int> path,
        MacroStep replacement)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("Step path cannot be empty.", nameof(path));
        }

        if (path.Count == 1)
        {
            var result = steps.ToList();
            result[CheckedIndex(path[0], result.Count)] = replacement;
            return result;
        }

        var parentPath = path.Take(path.Count - 1).ToArray();
        var childIndex = path[^1];
        var parent = GetAtPath(steps, parentPath);
        MacroStep updatedParent = parent switch
        {
            RepeatStep repeat => repeat with { Steps = ReplaceAtPath(repeat.Steps, [childIndex], replacement) },
            PixelWhenStep pixel => pixel with { ThenSteps = ReplaceAtPath(pixel.ThenSteps, [childIndex], replacement) },
            _ => throw new InvalidOperationException($"Step '{parent.GetType().Name}' cannot contain child steps.")
        };

        return ReplaceAtPath(steps, parentPath, updatedParent);
    }

    public static IReadOnlyList<MacroStep> ReplaceAtPathWithLinkedPressRelease(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<int> path,
        MacroStep replacement)
    {
        var current = GetAtPath(steps, path);
        var updated = ReplaceAtPath(steps, path, replacement);
        var pairedReplacement = TryCreatePairedReplacement(current, replacement);
        if (pairedReplacement is null)
        {
            return updated;
        }

        var pairPath = FindLinkedPressReleasePairPath(steps, path, current);
        return pairPath is null
            ? updated
            : ReplaceAtPath(updated, pairPath, pairedReplacement);
    }

    public static IReadOnlyList<MacroStep> MoveAtPath(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<int> sourcePath,
        IReadOnlyList<int> targetParentPath,
        int targetInsertIndex)
    {
        if (sourcePath.Count == 0)
        {
            throw new ArgumentException("Source path cannot be empty.", nameof(sourcePath));
        }

        if (PathStartsWith(targetParentPath, sourcePath))
        {
            throw new InvalidOperationException("Cannot move a step into itself.");
        }

        var step = GetAtPath(steps, sourcePath);
        var afterDelete = DeleteAtPath(steps, sourcePath);
        var adjustedTargetParentPath = AdjustPathAfterDelete(targetParentPath, sourcePath);
        var adjustedInsertIndex = targetInsertIndex;
        var sourceParentPath = sourcePath.Take(sourcePath.Count - 1).ToArray();
        if (PathsEqual(sourceParentPath, targetParentPath) && sourcePath[^1] < targetInsertIndex)
        {
            adjustedInsertIndex--;
        }

        return InsertAtPath(afterDelete, adjustedTargetParentPath, adjustedInsertIndex, [step]);
    }

    public static IReadOnlyList<MacroStep> MoveManyAtPath(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<IReadOnlyList<int>> sourcePaths,
        IReadOnlyList<int> targetParentPath,
        int targetInsertIndex)
    {
        var normalizedSources = NormalizeSourcePaths(sourcePaths);
        if (normalizedSources.Count == 0)
        {
            return steps.ToList();
        }

        foreach (var sourcePath in normalizedSources)
        {
            if (PathStartsWith(targetParentPath, sourcePath))
            {
                throw new InvalidOperationException("Cannot move a step into itself.");
            }
        }

        var movingSteps = normalizedSources
            .Select(path => GetAtPath(steps, path))
            .ToList();

        var result = steps;
        var adjustedTargetParentPath = targetParentPath.ToArray();
        var adjustedInsertIndex = targetInsertIndex;
        foreach (var sourcePath in normalizedSources.OrderByDescending(path => path, StepPathComparer.Instance))
        {
            var sourceParentPath = sourcePath.Take(sourcePath.Count - 1).ToArray();
            if (PathsEqual(sourceParentPath, adjustedTargetParentPath) && sourcePath[^1] < adjustedInsertIndex)
            {
                adjustedInsertIndex--;
            }

            adjustedTargetParentPath = AdjustPathAfterDelete(adjustedTargetParentPath, sourcePath).ToArray();
            result = DeleteAtPath(result, sourcePath);
        }

        return InsertAtPath(result, adjustedTargetParentPath, adjustedInsertIndex, movingSteps);
    }

    private static MacroStep? TryCreatePairedReplacement(MacroStep current, MacroStep replacement)
    {
        return (current, replacement) switch
        {
            (KeyStep { Kind: KeyActionKind.Down or KeyActionKind.Up },
                KeyStep { Kind: KeyActionKind.Down or KeyActionKind.Up } edited)
                => edited with { Kind = Opposite(edited.Kind) },
            (MouseButtonStep { Kind: ButtonActionKind.Down or ButtonActionKind.Up },
                MouseButtonStep { Kind: ButtonActionKind.Down or ButtonActionKind.Up } edited)
                => edited with { Kind = Opposite(edited.Kind), CoordinateMode = null, X = null, Y = null },
            _ => null
        };
    }

    private static IReadOnlyList<int>? FindLinkedPressReleasePairPath(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<int> path,
        MacroStep current)
    {
        if (path.Count == 0)
        {
            return null;
        }

        var parentPath = path.Take(path.Count - 1).ToArray();
        var siblings = GetChildrenAtPath(steps, parentPath);
        var index = path[^1];
        if (index < 0 || index >= siblings.Count)
        {
            return null;
        }

        var forward = FindPairIndex(siblings, index, current, +1);
        var backward = FindPairIndex(siblings, index, current, -1);
        var match = (forward, backward) switch
        {
            (null, null) => (int?)null,
            (int f, null) => f,
            (null, int b) => b,
            (int f, int b) => Math.Abs(f - index) <= Math.Abs(index - b) ? f : b
        };

        return match is null ? null : parentPath.Concat([match.Value]).ToArray();
    }

    private static IReadOnlyList<MacroStep> GetChildrenAtPath(IReadOnlyList<MacroStep> steps, IReadOnlyList<int> parentPath)
    {
        if (parentPath.Count == 0)
        {
            return steps;
        }

        return GetAtPath(steps, parentPath) switch
        {
            RepeatStep repeat => repeat.Steps,
            PixelWhenStep pixel => pixel.ThenSteps,
            _ => throw new InvalidOperationException("Step cannot contain child steps.")
        };
    }

    private static int? FindPairIndex(IReadOnlyList<MacroStep> siblings, int startIndex, MacroStep current, int direction)
    {
        for (var i = startIndex + direction; i >= 0 && i < siblings.Count; i += direction)
        {
            if (IsMatchingOpposite(current, siblings[i]))
            {
                return i;
            }

            if (IsSameInputFamily(current, siblings[i]))
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsMatchingOpposite(MacroStep left, MacroStep right)
    {
        return (left, right) switch
        {
            (KeyStep leftKey, KeyStep rightKey)
                => leftKey.Key == rightKey.Key
                   && leftKey.Modifiers == rightKey.Modifiers
                   && IsOpposite(leftKey.Kind, rightKey.Kind),
            (MouseButtonStep leftButton, MouseButtonStep rightButton)
                => leftButton.Button == rightButton.Button
                   && IsOpposite(leftButton.Kind, rightButton.Kind),
            _ => false
        };
    }

    private static bool IsSameInputFamily(MacroStep current, MacroStep candidate)
    {
        return (current, candidate) switch
        {
            (KeyStep, KeyStep) => true,
            (MouseButtonStep, MouseButtonStep) => true,
            _ => false
        };
    }

    private static KeyActionKind Opposite(KeyActionKind kind)
    {
        return kind == KeyActionKind.Down ? KeyActionKind.Up : KeyActionKind.Down;
    }

    private static ButtonActionKind Opposite(ButtonActionKind kind)
    {
        return kind == ButtonActionKind.Down ? ButtonActionKind.Up : ButtonActionKind.Down;
    }

    private static bool IsOpposite(KeyActionKind left, KeyActionKind right)
    {
        return (left == KeyActionKind.Down && right == KeyActionKind.Up)
            || (left == KeyActionKind.Up && right == KeyActionKind.Down);
    }

    private static bool IsOpposite(ButtonActionKind left, ButtonActionKind right)
    {
        return (left == ButtonActionKind.Down && right == ButtonActionKind.Up)
            || (left == ButtonActionKind.Up && right == ButtonActionKind.Down);
    }

    private static IReadOnlyList<int> AdjustPathAfterDelete(IReadOnlyList<int> path, IReadOnlyList<int> deletedPath)
    {
        if (path.Count == 0 || deletedPath.Count == 0)
        {
            return path.ToArray();
        }

        var deletedParent = deletedPath.Take(deletedPath.Count - 1).ToArray();
        var pathParent = path.Take(Math.Max(0, deletedParent.Length)).ToArray();
        if (PathsEqual(pathParent, deletedParent) && path.Count > deletedParent.Length)
        {
            var adjusted = path.ToArray();
            var level = deletedParent.Length;
            if (adjusted[level] > deletedPath[^1])
            {
                adjusted[level]--;
            }

            return adjusted;
        }

        return path.ToArray();
    }

    private static bool PathStartsWith(IReadOnlyList<int> path, IReadOnlyList<int> prefix)
    {
        return path.Count >= prefix.Count
            && !prefix.Where((value, index) => path[index] != value).Any();
    }

    private static bool PathsEqual(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        return left.Count == right.Count && !left.Where((value, index) => right[index] != value).Any();
    }

    private static IReadOnlyList<IReadOnlyList<int>> NormalizeSourcePaths(IReadOnlyList<IReadOnlyList<int>> sourcePaths)
    {
        var unique = new List<IReadOnlyList<int>>();
        foreach (var path in sourcePaths.Where(path => path.Count > 0).Select(path => path.ToArray()))
        {
            if (!unique.Any(existing => PathsEqual(existing, path)))
            {
                unique.Add(path);
            }
        }

        unique.Sort(StepPathComparer.Instance);
        return unique
            .Where(path => !unique.Any(other => !PathsEqual(other, path) && PathStartsWith(path, other)))
            .ToList();
    }

    private sealed class StepPathComparer : IComparer<IReadOnlyList<int>>
    {
        public static StepPathComparer Instance { get; } = new();

        public int Compare(IReadOnlyList<int>? x, IReadOnlyList<int>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var count = Math.Min(x.Count, y.Count);
            for (var i = 0; i < count; i++)
            {
                var compared = x[i].CompareTo(y[i]);
                if (compared != 0)
                {
                    return compared;
                }
            }

            return x.Count.CompareTo(y.Count);
        }
    }

    private static int CheckedIndex(int index, int count)
    {
        if (index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Step index {index} is outside 0..{count - 1}.");
        }

        return index;
    }
}
