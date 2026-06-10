using System.Diagnostics;
using System.IO;
using System.Windows;
using MacroHid.Core;
using Microsoft.Win32;

namespace MacroStudio;

public partial class MainWindow : Window
{
    private const string SampleMacro = """
    {
      "version": 1,
      "name": "baseline",
      "steps": [
        { "type": "key.tap", "key": "A", "modifiers": ["LeftCtrl"], "holdMs": 5 },
        { "type": "mouse.move", "mode": "relative", "x": 25, "y": -10, "durationMs": 0 },
        { "type": "mouse.wheel", "vertical": -1, "horizontal": 0 },
        { "type": "consumer.tap", "control": "VolumeUp" },
        { "type": "wait", "ms": 2 },
        {
          "type": "pixel.when",
          "scope": "screen",
          "x": 100,
          "y": 200,
          "r": 10,
          "g": 20,
          "b": 30,
          "tolerance": 4,
          "then": [
            { "type": "key.down", "key": "Enter" }
          ]
        }
      ]
    }
    """;

    public MainWindow()
    {
        InitializeComponent();
        MacroEditor.Text = SampleMacro;
        ValidateCurrentMacro();
    }

    private void OpenMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MacroHID macro (*.mcrx)|*.mcrx|JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Open macro"
        };

        if (dialog.ShowDialog(this) == true)
        {
            MacroEditor.Text = File.ReadAllText(dialog.FileName);
            ValidateCurrentMacro();
            StatusText.Text = Path.GetFileName(dialog.FileName);
        }
    }

    private void SaveMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "MacroHID macro (*.mcrx)|*.mcrx|JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Save macro",
            DefaultExt = ".mcrx"
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, MacroEditor.Text);
            StatusText.Text = Path.GetFileName(dialog.FileName);
        }
    }

    private void ValidateMacro_Click(object sender, RoutedEventArgs e)
    {
        ValidateCurrentMacro();
    }

    private void Probe_Click(object sender, RoutedEventArgs e)
    {
        var histogram = new LatencyHistogram();
        var stopwatch = Stopwatch.StartNew();
        var intervalTicks = Stopwatch.Frequency / 1_000;
        var nextTick = stopwatch.ElapsedTicks + intervalTicks;

        for (var i = 0; i < 250; i++)
        {
            while (stopwatch.ElapsedTicks < nextTick)
            {
                Thread.SpinWait(32);
            }

            var actual = stopwatch.ElapsedTicks;
            histogram.RecordMicroseconds(Math.Abs(actual - nextTick) * 1_000_000 / Stopwatch.Frequency);
            _ = HidReportEncoder.EncodeKeyboard(new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.None, TimeSpan.Zero));
            nextTick += intervalTicks;
        }

        LatencyText.Text = histogram.Summary();
    }

    private void ValidateCurrentMacro()
    {
        try
        {
            var document = McrxParser.Parse(MacroEditor.Text);
            var scheduled = MacroScheduler.Compile(document, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

            MacroNameText.Text = document.Name;
            StepCountText.Text = scheduled.Count.ToString();
            StepList.Items.Clear();

            foreach (var step in scheduled)
            {
                StepList.Items.Add($"{step.DueTick}: {Describe(step.Step)}");
            }

            StatusText.Text = "Macro valid";
        }
        catch (Exception ex)
        {
            MacroNameText.Text = "-";
            StepCountText.Text = "0";
            StepList.Items.Clear();
            StepList.Items.Add(ex.Message);
            StatusText.Text = "Macro invalid";
        }
    }

    private static string Describe(MacroStep step)
    {
        return step switch
        {
            KeyStep key => $"key.{key.Kind.ToString().ToLowerInvariant()} {key.Modifiers}+{key.Key}",
            MouseMoveStep move => $"mouse.move {move.Mode} x={move.X} y={move.Y} buttons={move.Buttons}",
            MouseButtonStep button => $"mouse.{button.Kind.ToString().ToLowerInvariant()} {button.Button}",
            MouseWheelStep wheel => $"mouse.wheel vertical={wheel.Vertical} horizontal={wheel.Horizontal}",
            ConsumerStep consumer => $"consumer.{consumer.Kind.ToString().ToLowerInvariant()} {consumer.Control}",
            PixelWhenStep pixel => $"pixel.when {pixel.Condition.Coordinate.Scope} x={pixel.Condition.Coordinate.X} y={pixel.Condition.Coordinate.Y}",
            _ => step.GetType().Name
        };
    }
}
