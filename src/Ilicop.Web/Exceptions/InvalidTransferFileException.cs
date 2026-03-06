using System;

namespace Geowerkstatt.Ilicop.Web.Exceptions;

[Serializable]
internal class InvalidTransferFileException : Exception
{
    public InvalidTransferFileException()
    {
    }

    public InvalidTransferFileException(string message)
        : base(message)
    {
    }

    public InvalidTransferFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
