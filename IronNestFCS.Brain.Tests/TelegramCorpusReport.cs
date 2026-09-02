using IronNestFCS.Abstractions.Brain;

internal sealed record TelegramCorpusReport(
    int MessagesScanned,
    int MessagesWithTacticalSemantics,
    int FullyParsed,
    int PartiallyParsed,
    int Unsupported,
    IReadOnlyList<TelegramSemanticEvent> Messages)
{
    public string ToSummary()
    {
        return $"scanned={MessagesScanned}; tactical={MessagesWithTacticalSemantics}; "
            + $"fully-parsed={FullyParsed}; partial={PartiallyParsed}; unsupported={Unsupported}";
    }
}
