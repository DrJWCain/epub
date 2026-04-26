using System.IO.Compression;
using Epub.Core.Models;
using Epub.Core.Parsing;

namespace Epub.Core;

public sealed class EpubReader : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Lock _archiveLock = new();
    private bool _disposed;

    public Book Book { get; }

    private EpubReader(ZipArchive archive, Book book)
    {
        _archive = archive;
        Book = book;
    }

    public static EpubReader Open(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            return Open(stream, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static EpubReader Open(Stream stream, bool leaveOpen = false)
    {
        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
        try
        {
            var book = ParseBook(archive);
            return new EpubReader(archive, book);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public static Task<EpubReader> OpenAsync(string path) => Task.Run(() => Open(path));

    /// <summary>Resolve an OPF-relative href to a zip-internal path.</summary>
    public string ResolveHref(string opfRelativeHref)
        => ZipPathResolver.Resolve(Book.OpfBaseDir, opfRelativeHref);

    /// <summary>Open a resource by its zip-internal path. Caller disposes the stream.</summary>
    public Stream OpenResource(string zipPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_archiveLock)
        {
            var entry = _archive.GetEntry(zipPath)
                ?? throw new FileNotFoundException($"Resource not found in EPUB: {zipPath}");
            return entry.Open();
        }
    }

    public bool ResourceExists(string zipPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_archiveLock)
        {
            return _archive.GetEntry(zipPath) is not null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archive.Dispose();
    }

    private static Book ParseBook(ZipArchive archive)
    {
        var containerEntry = archive.GetEntry(ContainerXmlParser.ContainerPath)
            ?? throw new InvalidEpubException($"EPUB is missing {ContainerXmlParser.ContainerPath}.");

        string opfPath;
        using (var containerStream = containerEntry.Open())
            opfPath = ContainerXmlParser.FindOpfPath(containerStream);

        var opfEntry = archive.GetEntry(opfPath)
            ?? throw new InvalidEpubException($"OPF file not found at {opfPath}.");

        OpfParseResult opfResult;
        using (var opfStream = opfEntry.Open())
            opfResult = OpfParser.Parse(opfStream);

        var opfBaseDir = ZipPathResolver.DirectoryOf(opfPath);

        var toc = TryParseToc(archive, opfBaseDir, opfResult.NavHref, opfResult.NcxHref);

        return new Book
        {
            Metadata = opfResult.Metadata,
            Manifest = opfResult.Manifest,
            Spine = opfResult.Spine,
            Toc = toc,
            OpfPath = opfPath,
            OpfBaseDir = opfBaseDir,
            CoverImageHref = opfResult.CoverHref,
        };
    }

    private static Toc? TryParseToc(ZipArchive archive, string opfBaseDir, string? navHref, string? ncxHref)
    {
        if (navHref is not null)
        {
            var navPath = ZipPathResolver.Resolve(opfBaseDir, navHref);
            var navEntry = archive.GetEntry(navPath);
            if (navEntry is not null)
            {
                using var navStream = navEntry.Open();
                return NavParser.Parse(navStream);
            }
        }

        if (ncxHref is not null)
        {
            var ncxPath = ZipPathResolver.Resolve(opfBaseDir, ncxHref);
            var ncxEntry = archive.GetEntry(ncxPath);
            if (ncxEntry is not null)
            {
                using var ncxStream = ncxEntry.Open();
                return NcxParser.Parse(ncxStream);
            }
        }

        return null;
    }
}
