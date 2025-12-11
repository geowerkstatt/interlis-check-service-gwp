using Geowerkstatt.Ilicop.Web.Ilitools;

namespace Geowerkstatt.Ilicop.Web;

public class ImportRequest : IlitoolsRequest
{
    /// <summary>
    /// Gets or sets the full path to the database file to import data into.
    /// </summary>
    public required string DbFilePath { get; init; }

    /// <summary>
    /// The name of the dataset as which the data should be imported.
    /// </summary>
    public required string Dataset { get; init; }

    /// <summary>
    /// The path to the SQL script to be executed after the import.
    /// </summary>
    public string PostSqlScriptPath { get; init; }
}
