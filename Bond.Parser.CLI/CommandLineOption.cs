namespace Bond.Parser.CLI;

internal readonly record struct CommandLineOption(string Name, string? InlineValue)
{
    internal static CommandLineOption Parse(string argument)
    {
        var separator = argument.IndexOf('=');
        return separator < 0
            ? new CommandLineOption(argument, null)
            : new CommandLineOption(argument[..separator], argument[(separator + 1)..]);
    }

    internal bool TryReadValue(string[] arguments, ref int index, out string value)
    {
        if (InlineValue is not null)
        {
            value = InlineValue;
            return true;
        }

        var next = index + 1;
        if (next < arguments.Length && !arguments[next].StartsWith('-'))
        {
            index = next;
            value = arguments[next];
            return true;
        }

        value = "";
        return false;
    }
}
