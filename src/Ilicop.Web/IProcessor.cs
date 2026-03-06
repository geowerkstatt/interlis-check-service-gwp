using Geowerkstatt.Ilicop.Web.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Geowerkstatt.Ilicop.Web;

/// <summary>
/// Does further processing after validation.
/// </summary>
public interface IProcessor
{
    /// <summary>
    /// Runs the processor for the specified job and profile.
    /// </summary>
    /// <param name="validator">The validator with which the processor should run.</param>
    /// <param name="transferFile">The transfer file to be processed.</param>
    /// <param name="profile">The profile with which the processing should be done.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> to cancel the asynchronous operation.</param>
    Task Run(IValidator validator, NamedFile transferFile, Profile profile, CancellationToken cancellationToken);
}
