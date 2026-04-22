using Geowerkstatt.Ilicop.Web.Contracts;
using Geowerkstatt.Ilicop.Web.Exceptions;
using Geowerkstatt.Ilicop.Web.Ilitools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Geowerkstatt.Ilicop.Web;

/// <summary>
/// Processor for all GWP related tasks.
/// </summary>
public class GwpProcessor : IProcessor
{
    private readonly IFileProvider fileProvider;
    private readonly DirectoryInfo configDir;
    private readonly ILogger<GwpProcessor> logger;
    private readonly GwpProcessorOptions gwpProcessorOptions;
    private readonly IlitoolsExecutor ilitoolsExecutor;
    private readonly IEnumerable<string> allowedFileExtensionsForZippedFiles = [".xtf"];

    public IEnumerable<string> SupportedFileExtensions => [".xtf", ".zip"];

    public GwpProcessor(
        IOptions<GwpProcessorOptions> gwpProcessorOptions,
        IFileProvider fileProvider,
        IlitoolsExecutor ilitoolsExecutor,
        ILogger<GwpProcessor> logger)
    {
        this.fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.gwpProcessorOptions = gwpProcessorOptions?.Value ?? throw new ArgumentNullException(nameof(gwpProcessorOptions));
        this.ilitoolsExecutor = ilitoolsExecutor ?? throw new ArgumentNullException(nameof(ilitoolsExecutor));

        if (this.gwpProcessorOptions.ConfigDir != null)
            this.configDir = new DirectoryInfo(this.gwpProcessorOptions.ConfigDir);
    }

    /// <inheritdoc />
    public async Task Run(IValidator validator, NamedFile transferFile, Profile profile, CancellationToken cancellationToken)
    {
        if (configDir == null || !Directory.Exists(Path.Combine(configDir.FullName, profile.Id)))
        {
            throw new GwpProcessorException($"No configuration directory found for profile <{profile.Id}>. Expected path: <{configDir}>");
        }

        Exception validationException = null;
        var jobId = validator.Id;
        fileProvider.Initialize(jobId);

        var xtfFilesToValidate = ExtractXtfFiles(transferFile);

        var dataGpkgFilePath = CopyTemplateGpkg(profile);

        await ImportXtfFilesToGpkg(xtfFilesToValidate, dataGpkgFilePath, profile, cancellationToken);

        var translatedFile = await CreateTranslatedTransferFile(dataGpkgFilePath, transferFile, profile, cancellationToken);

        try
        {
            await validator.ExecuteAsync(translatedFile.FileName, profile, cancellationToken);
        }
        catch (ValidationFailedException ex)
        {
            validationException = ex;
        }

        TryGetPostSqlScriptPath(profile, out var postSqlScriptPath);
        var importLogTransferFileExitCode = await ImportLogToGpkg(fileProvider, dataGpkgFilePath, postSqlScriptPath, cancellationToken);
        CreateErrorStatisticCsv(dataGpkgFilePath);
        TryCopyQgisServiceFile(fileProvider, profile);
        CreateZip(jobId, transferFile, profile);

        if (validationException != null) throw validationException;
    }

    private List<NamedFile> ExtractXtfFiles(NamedFile transferFile)
    {
        List<NamedFile> xtfFiles = new List<NamedFile>();
        var transferFileIsZip = Path.GetExtension(transferFile.FilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase);
        if (transferFileIsZip)
        {
            xtfFiles = UnzipTransferFile(transferFile);
        }
        else
        {
            xtfFiles.Add(transferFile);
        }

        return xtfFiles;
    }

    private async Task ImportXtfFilesToGpkg(List<NamedFile> xtfFiles, string dataGpkgFilePath, Profile profile, CancellationToken cancellationToken)
    {
        foreach (var xtfFile in xtfFiles)
        {
            var importXtfExitCode = await ImportTransferFileToGpkg(fileProvider, dataGpkgFilePath, xtfFile.FileName, cancellationToken);
            if (importXtfExitCode != 0)
            {
                throw new InvalidTransferFileException($"Import of transfer file <{xtfFile.FileName}> to GeoPackage failed for profile <{profile.Id}> with exit code <{importXtfExitCode}>.");
            }
        }
    }

    private List<NamedFile> UnzipTransferFile(NamedFile transferFile)
    {
        logger.LogInformation("Unzipping compressed file <{TransferFile}>", transferFile);

        using var archive = ZipFile.OpenRead(transferFile.FilePath);

        var unsupportedFile = archive.Entries
            .FirstOrDefault(e => !allowedFileExtensionsForZippedFiles.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase));

        if (unsupportedFile != null)
        {
            throw new UnknownExtensionException(Path.GetExtension(unsupportedFile.Name), $"The ZIP file contains unsupported file types.");
        }

        var xtfFiles = new List<NamedFile>();
        foreach (var entry in archive.Entries)
        {
            var sanitizedFileName = Path.ChangeExtension(
                Path.GetRandomFileName(),
                entry.Name.GetSanitizedFileExtension(allowedFileExtensionsForZippedFiles));
            var extractedFilePath = Path.Combine(fileProvider.HomeDirectory.FullName, sanitizedFileName);
            var extractedFile = new NamedFile(extractedFilePath, entry.Name);

            entry.ExtractToFile(extractedFile.FilePath);
            xtfFiles.Add(extractedFile);
        }

        return xtfFiles;
    }

    private async Task<NamedFile> CreateTranslatedTransferFile(string gpkgPath, NamedFile transferFile, Profile profile, CancellationToken cancellationToken)
    {
        var translatedTransferFile = GetTranslatedTransferFile(transferFile);

        var exportRequest = new ExportRequest
        {
            FileName = translatedTransferFile.FileName,
            FilePath = translatedTransferFile.FilePath,
            Profile = profile,
            DbFilePath = gpkgPath,
            Dataset = "Data",
        };

        var resultCode = await ilitoolsExecutor.ExportFromGpkgAsync(exportRequest, cancellationToken);

        if (resultCode != 0) throw new GwpProcessorException("An error occurred during export of the translated transfer file.");

        return translatedTransferFile;
    }

    private bool IsTranslationNeeded(string gpkgPath)
    {
        var models = GetColumnFromSqliteTable(gpkgPath, "T_ILI2DB_MODEL", "modelName").Select(m => m.ToString());
        var topics = GetColumnFromSqliteTable(gpkgPath, "T_ILI2DB_BASKET", "topic").Select(m => m.ToString());

        return GetBasketTopicsNotInModels(topics, models).Any();
    }

    internal IEnumerable<string> GetBasketTopicsNotInModels(IEnumerable<string> topics, IEnumerable<string> models)
    {
        var splitModels = models.SelectMany(r => r.Replace("{", "").Replace("}", "").Split(' ')).Distinct().ToList();
        var importedBasketsModel = topics.Select(r => r.Split('.')[0]).Distinct().ToList();

        return importedBasketsModel.Except(splitModels);
    }

    private string CopyTemplateGpkg(Profile profile)
    {
        var templateGpkgFilePath = Path.Combine(configDir.FullName, profile.Id, gwpProcessorOptions.DataGpkgFileName);

        if (!File.Exists(templateGpkgFilePath))
        {
            throw new GwpProcessorException($"Template GeoPackage file for profile <{profile.Id}> could not be found. Expected path: <{templateGpkgFilePath}>.");
        }

        var dataGpkgFilePath = Path.Combine(fileProvider.HomeDirectory.FullName, gwpProcessorOptions.DataGpkgFileName);

        using (var destGpkgFileStream = fileProvider.CreateFile(dataGpkgFilePath))
        using (var sourceGpkgFileStream = File.OpenRead(templateGpkgFilePath))
        {
            sourceGpkgFileStream.CopyTo(destGpkgFileStream);
        }

        return dataGpkgFilePath;
    }

    private void CreateErrorStatisticCsv(string dataGpkgFilePath)
    {
        var errorStatCsvFilePath = Path.Combine(fileProvider.HomeDirectory.FullName, gwpProcessorOptions.ErrorStatisticCsvFileName);

        try
        {
            var connectionString = $"Data Source={dataGpkgFilePath}";
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"v_error_stat\"";

            using var reader = command.ExecuteReader();
            using var writer = new StreamWriter(errorStatCsvFilePath);

            // Write header
            writer.WriteLine("Priority,Number,Description");

            // Write data rows
            while (reader.Read())
            {
                var priority = reader.GetValue(0);
                var number = reader.GetValue(1);
                var description = reader.GetValue(2) as string ?? string.Empty;
                description = description.Replace("\"", "\"\""); // Escape double quotes for CSV format
                writer.WriteLine($"{priority},{number},\"{description}\"");
            }

            SqliteConnection.ClearAllPools();
        }
        catch (SqliteException ex)
        {
            throw new GwpProcessorException($"Failed to create error statistic CSV. View 'v_error_stat' might not exist in data.gpkg.", ex);
        }
        catch (Exception ex)
        {
            throw new GwpProcessorException("Failed to create error statistic CSV.", ex);
        }
    }

    private bool TryCopyQgisServiceFile(IFileProvider fileProvider, Profile profile)
    {
        var serviceFile = Path.Combine(configDir.FullName, profile.Id, gwpProcessorOptions.QgisProjectFileName);
        if (!File.Exists(serviceFile))
        {
            logger.LogInformation($"QGIS service file for profile <{profile.Id}> could not be found. Expected path: <{serviceFile}>.");
            return false;
        }

        try
        {
            logger.LogInformation("Copying QGIS project file for profile <{ProfileId}>.", profile.Id);
            File.Copy(serviceFile, Path.Combine(fileProvider.HomeDirectory.FullName, gwpProcessorOptions.QgisProjectFileName), true);
        }
        catch (SystemException ex)
        {
            throw new GwpProcessorException($"Failed to copy QGIS project file for profile <{profile.Id}>.", ex);
        }

        return true;
    }

    private async Task<int> ImportLogToGpkg(IFileProvider fileProvider, string gpkgFilePath, string postSqlScriptPath, CancellationToken cancellationToken)
    {
        var logFileName = fileProvider.GetFiles().FirstOrDefault(f => f.EndsWith("_log.xtf", StringComparison.InvariantCultureIgnoreCase));
        var logFilePath = Path.Combine(fileProvider.HomeDirectory.FullName, logFileName);

        var logFileImportRequest = new ImportRequest
        {
            FilePath = logFilePath,
            FileName = logFileName,
            DbFilePath = gpkgFilePath,
            Dataset = "Logs",
            PostSqlScriptPath = postSqlScriptPath,
        };

        return await ilitoolsExecutor.ImportToGpkgAsync(logFileImportRequest, cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetPostSqlScriptPath(Profile profile, out string postSqlScriptPath)
    {
        postSqlScriptPath = Path.Combine(configDir.FullName, profile.Id, gwpProcessorOptions.PostSqlScriptFileName);

        if (!File.Exists(postSqlScriptPath))
        {
            logger.LogInformation("No post sql script found at <{PostSqlScriptPath}>.", postSqlScriptPath);
            postSqlScriptPath = null;
            return false;
        }

        return true;
    }

    private async Task<int> ImportTransferFileToGpkg(IFileProvider fileProvider, string gpkgFilePath, string transferFile, CancellationToken cancellationToken)
    {
        var transferFilePath = Path.Combine(fileProvider.HomeDirectory.FullName, transferFile);

        var transferFileImportRequest = new ImportRequest
        {
            FilePath = transferFilePath,
            FileName = transferFile,
            DbFilePath = gpkgFilePath,
            Dataset = "Data",
        };

        return await ilitoolsExecutor.ImportToGpkgAsync(transferFileImportRequest, cancellationToken).ConfigureAwait(false);
    }

    private void CreateZip(Guid jobId, NamedFile transferFile, Profile profile)
    {
        logger.LogInformation("Creating ZIP for job <{JobId}>.", jobId);

        var filesToZip = GetFilesToZip(transferFile, profile);

        var zipFileStream = fileProvider.CreateFile(gwpProcessorOptions.ZipFileName);
        using (var archive = new ZipArchive(zipFileStream, ZipArchiveMode.Create))
        {
            foreach (var fileToZip in filesToZip)
            {
                archive.CreateEntryFromFile(fileToZip.FilePath, fileToZip.DisplayName, CompressionLevel.Optimal);
                logger.LogTrace("Added file <{FileName}> to ZIP for job <{JobId}>", fileToZip.DisplayName, jobId);
            }
        }

        logger.LogInformation("Successfully created ZIP for job <{JobId}>.", jobId);
    }

    private List<NamedFile> GetFilesToZip(NamedFile transferFile, Profile profile)
    {
        var filesToZip = new List<NamedFile>();
        filesToZip.AddRange(GetLogFilesToZip(fileProvider));
        filesToZip.AddRange(GetAdditionalFilesToZip(fileProvider, profile));

        // Add GeoPackage if exists
        var gpkgFilePath = Path.Combine(fileProvider.HomeDirectory.FullName, gwpProcessorOptions.DataGpkgFileName);
        if (File.Exists(gpkgFilePath))
            filesToZip.Add(new NamedFile(gpkgFilePath, gwpProcessorOptions.DataGpkgFileName));

        // Add error statistic CSV if exists
        var errorStatCsvFilePath = Path.Combine(fileProvider.HomeDirectory.FullName, gwpProcessorOptions.ErrorStatisticCsvFileName);
        if (File.Exists(errorStatCsvFilePath))
            filesToZip.Add(new NamedFile(errorStatCsvFilePath, gwpProcessorOptions.ErrorStatisticCsvFileName));

        // Add translated transfer file if the uploaded transfer file(s) were not already the correct language
        if (IsTranslationNeeded(gpkgFilePath))
            filesToZip.Add(GetTranslatedTransferFile(transferFile));

        return filesToZip;
    }

    private IEnumerable<NamedFile> GetLogFilesToZip(IFileProvider fileProvider)
    {
        return fileProvider.GetFiles()
            .Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("_log", true, CultureInfo.InvariantCulture))
            .Select(f => new NamedFile(Path.Combine(fileProvider.HomeDirectory.FullName, f), $"log{Path.GetExtension(f)}"));
    }

    private IEnumerable<NamedFile> GetAdditionalFilesToZip(IFileProvider fileProvider, Profile profile)
    {
        var additionalFilesDirPath = Path.Combine(configDir.FullName, profile.Id, gwpProcessorOptions.AdditionalFilesFolderName);

        if (!Directory.Exists(additionalFilesDirPath))
            return Enumerable.Empty<NamedFile>();

        return Directory.GetFiles(additionalFilesDirPath)
            .Select(f => new NamedFile(f));
    }

    private IEnumerable<object> GetColumnFromSqliteTable(string dbFilePath, string tableName, string columnName)
    {
        var connectionString = $"Data Source={dbFilePath}";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = $"SELECT [{columnName}] FROM [{tableName}]";
#pragma warning restore CA2100

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetValue(0);
        }

        SqliteConnection.ClearAllPools();
    }

    private NamedFile GetTranslatedTransferFile(NamedFile transferFile)
    {
        var suffix = "_translated.xtf";
        var fileName = $"{Path.GetFileNameWithoutExtension(transferFile.FileName)}{suffix}";
        var displayName = $"{Path.GetFileNameWithoutExtension(transferFile.DisplayName)}{suffix}";
        var path = Path.Combine(fileProvider.HomeDirectory.FullName, fileName);
        return new NamedFile(path, displayName);
    }
}
