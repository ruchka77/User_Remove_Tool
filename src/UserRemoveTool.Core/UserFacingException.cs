namespace UserRemoveTool.Core;

/// <summary>An error whose message is Hebrew text that may be shown to the operator as is.</summary>
public sealed class UserFacingException(string message, Exception? inner = null) : Exception(message, inner);
