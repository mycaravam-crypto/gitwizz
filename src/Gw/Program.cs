using Gitwizz;
using Spectre.Console;

var err = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
if (Console.IsErrorRedirected) err.Profile.Width = 120;
if (Console.IsOutputRedirected) AnsiConsole.Profile.Width = 120;

return Cli.Dispatch(args, err);
