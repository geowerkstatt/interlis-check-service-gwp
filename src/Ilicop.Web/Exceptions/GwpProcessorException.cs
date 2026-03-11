using System;

namespace Geowerkstatt.Ilicop.Web
{
    /// <summary>
    /// The exception that is thrown when a XML transfer file cannot be parsed correctly.
    /// </summary>
    [Serializable]
    public class GwpProcessorException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GwpProcessorException"/> class.
        /// </summary>
        public GwpProcessorException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GwpProcessorException"/> class
        /// with a specified error <paramref name="message"/>.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public GwpProcessorException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GwpProcessorException"/> class
        /// with a specified error <paramref name="message"/> and a reference to the
        /// <paramref name="innerException"/> that is the cause of this exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public GwpProcessorException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
