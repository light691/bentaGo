namespace BentaGo.App.Dialogs;

/// <summary>Close signal: non-null Result confirms, null dismisses (Esc/system).</summary>
public sealed record DialogResultRequest<TResult>(TResult? Result) where TResult : class;
