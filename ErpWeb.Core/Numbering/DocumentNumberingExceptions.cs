namespace ErpWeb.Core.Numbering;

public sealed class DocumentNumberingNotConfiguredException : Exception
{
    public DocumentNumberingNotConfiguredException(string message) : base(message) { }
}

public sealed class DocumentNumberingConfigurationException : Exception
{
    public DocumentNumberingConfigurationException(string message) : base(message) { }
}

public sealed class DocumentNumberingOverflowException : Exception
{
    public DocumentNumberingOverflowException(string message) : base(message) { }
}

public sealed class DocumentNumberingConcurrencyException : Exception
{
    public DocumentNumberingConcurrencyException(string message) : base(message) { }
}

public sealed class DuplicateDocumentNumberException : Exception
{
    public DuplicateDocumentNumberException(string message) : base(message) { }
}
