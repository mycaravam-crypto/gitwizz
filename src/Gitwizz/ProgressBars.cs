using Spectre.Console;

namespace Gitwizz;

/// <summary>
/// Reports a command's phases as progress bars: Start begins a phase (total 0 = size unknown, shown as an indeterminate
/// bar) and completes the previous one; Advance may be called from any thread.
/// </summary>
public class ProgressBars(ProgressContext? ctx = null, Action<string>? log = null)
{
    ProgressTask? _task;

    /// <summary>Begins a phase of total units of work, completing the previous phase.</summary>
    public void Start(string description, int total = 0)
    {
        log?.Invoke(description);
        if (ctx == null) return;
        Complete();
        _task = ctx.AddTask(Markup.Escape(description), maxValue: Math.Max(total, 1));
        _task.IsIndeterminate = total <= 0;
    }

    /// <summary>Marks units of the current phase done. Thread-safe.</summary>
    public void Advance(int units = 1) => _task?.Increment(units);

    /// <summary>Resizes the current phase once its size is known (0 = unknown).</summary>
    public void Resize(int total)
    {
        if (_task == null) return;
        _task.IsIndeterminate = total <= 0;
        _task.MaxValue = Math.Max(total, 1);
    }

    /// <summary>Renames the current phase, e.g. to name the item being worked on.</summary>
    public void Describe(string description)
    {
        if (_task != null) _task.Description = Markup.Escape(description);
    }

    /// <summary>Completes the current phase.</summary>
    public void Complete()
    {
        if (_task == null) return;
        _task.IsIndeterminate = false;
        _task.Value = _task.MaxValue;
        _task.StopTask();
        _task = null;
    }

    /// <summary>
    /// Runs work with progress bars on console while it is an interactive terminal (cleared when done), else with none.
    /// log, if set, also receives each phase as it starts.
    /// </summary>
    public static T Show<T>(IAnsiConsole console, Func<ProgressBars, T> work, Action<string>? log = null)
    {
        if (Console.IsErrorRedirected || !console.Profile.Capabilities.Interactive) return work(new ProgressBars(null, log));
        return console.Progress().AutoClear(true).HideCompleted(false)
            .Columns(new SpinnerColumn(Spinner.Known.Dots) { Style = Style.Parse("steelblue1") }, new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn { CompletedStyle = Style.Parse("steelblue1"), FinishedStyle = Style.Parse("springgreen3") },
                new PercentageColumn(), new ElapsedTimeColumn())
            .Start(ctx =>
            {
                var progress = new ProgressBars(ctx, log);
                var result = work(progress);
                progress.Complete();
                return result;
            });
    }
}
