using System.Xml.Linq;
using Epub.Core.Models;

namespace Epub.Core.Parsing;

internal sealed record OpfParseResult(
    Metadata Metadata,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItem> Spine,
    string? NavHref,
    string? NcxHref,
    string? CoverHref);

internal static class OpfParser
{
    public static OpfParseResult Parse(Stream opfXml)
    {
        var doc = XDocument.Load(opfXml);
        var package = doc.Root
            ?? throw new InvalidEpubException("OPF document is empty.");

        var metadataEl = package.Element(Namespaces.Opf + "metadata")
            ?? throw new InvalidEpubException("OPF has no <metadata> element.");
        var manifestEl = package.Element(Namespaces.Opf + "manifest")
            ?? throw new InvalidEpubException("OPF has no <manifest> element.");
        var spineEl = package.Element(Namespaces.Opf + "spine")
            ?? throw new InvalidEpubException("OPF has no <spine> element.");

        var uniqueIdentifierId = (string?)package.Attribute("unique-identifier");

        var metadata = ParseMetadata(metadataEl, uniqueIdentifierId);
        var manifest = ParseManifest(manifestEl);
        var manifestById = manifest.ToDictionary(m => m.Id, StringComparer.Ordinal);

        var spine = ParseSpine(spineEl, manifestById);
        var navHref = manifest.FirstOrDefault(m => m.HasProperty("nav"))?.Href;
        var ncxItemId = (string?)spineEl.Attribute("toc");
        var ncxHref = ncxItemId is not null && manifestById.TryGetValue(ncxItemId, out var ncxItem)
            ? ncxItem.Href
            : manifest.FirstOrDefault(m => m.MediaType == "application/x-dtbncx+xml")?.Href;

        var coverHref = ResolveCoverHref(metadataEl, manifest, manifestById);

        return new OpfParseResult(metadata, manifest, spine, navHref, ncxHref, coverHref);
    }

    private static Metadata ParseMetadata(XElement metadataEl, string? uniqueIdentifierId)
    {
        var titles = metadataEl.Elements(Namespaces.Dc + "title").Select(e => e.Value.Trim()).Where(t => t.Length > 0).ToList();
        var creators = metadataEl.Elements(Namespaces.Dc + "creator").Select(e => e.Value.Trim()).Where(c => c.Length > 0).ToList();
        var identifiers = metadataEl.Elements(Namespaces.Dc + "identifier").ToList();

        string? identifier = null;
        if (uniqueIdentifierId is not null)
            identifier = identifiers.FirstOrDefault(i => (string?)i.Attribute("id") == uniqueIdentifierId)?.Value.Trim();
        identifier ??= identifiers.FirstOrDefault()?.Value.Trim();

        return new Metadata
        {
            Title = titles.FirstOrDefault() ?? "Untitled",
            Authors = creators,
            Publisher = metadataEl.Element(Namespaces.Dc + "publisher")?.Value.Trim(),
            Language = metadataEl.Element(Namespaces.Dc + "language")?.Value.Trim(),
            Identifier = identifier,
            PublicationDate = metadataEl.Element(Namespaces.Dc + "date")?.Value.Trim(),
            Description = metadataEl.Element(Namespaces.Dc + "description")?.Value.Trim(),
        };
    }

    private static List<ManifestItem> ParseManifest(XElement manifestEl)
    {
        var items = new List<ManifestItem>();
        foreach (var item in manifestEl.Elements(Namespaces.Opf + "item"))
        {
            var id = (string?)item.Attribute("id")
                ?? throw new InvalidEpubException("Manifest <item> missing id.");
            var href = (string?)item.Attribute("href")
                ?? throw new InvalidEpubException($"Manifest <item id='{id}'> missing href.");
            var mediaType = (string?)item.Attribute("media-type") ?? "application/octet-stream";
            var properties = ((string?)item.Attribute("properties") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            items.Add(new ManifestItem
            {
                Id = id,
                Href = href,
                MediaType = mediaType,
                Properties = properties,
            });
        }
        return items;
    }

    private static List<SpineItem> ParseSpine(XElement spineEl, Dictionary<string, ManifestItem> manifestById)
    {
        var spine = new List<SpineItem>();
        foreach (var itemref in spineEl.Elements(Namespaces.Opf + "itemref"))
        {
            var idref = (string?)itemref.Attribute("idref")
                ?? throw new InvalidEpubException("Spine <itemref> missing idref.");
            if (!manifestById.TryGetValue(idref, out var manifestItem))
                throw new InvalidEpubException($"Spine references manifest id '{idref}' which is not in the manifest.");

            var linear = !string.Equals((string?)itemref.Attribute("linear"), "no", StringComparison.OrdinalIgnoreCase);

            spine.Add(new SpineItem
            {
                IdRef = idref,
                Linear = linear,
                ManifestItem = manifestItem,
            });
        }
        return spine;
    }

    private static string? ResolveCoverHref(
        XElement metadataEl,
        IReadOnlyList<ManifestItem> manifest,
        Dictionary<string, ManifestItem> manifestById)
    {
        // EPUB 3: manifest item with properties="cover-image"
        var coverByProperty = manifest.FirstOrDefault(m => m.HasProperty("cover-image"));
        if (coverByProperty is not null)
            return coverByProperty.Href;

        // EPUB 2: <meta name="cover" content="cover-id"/>
        var coverMeta = metadataEl.Elements(Namespaces.Opf + "meta")
            .FirstOrDefault(m => string.Equals((string?)m.Attribute("name"), "cover", StringComparison.OrdinalIgnoreCase));
        var coverId = (string?)coverMeta?.Attribute("content");
        if (coverId is not null && manifestById.TryGetValue(coverId, out var coverById))
            return coverById.Href;

        // Fallback for broken OPFs (e.g. Write Great Code v3 declares <meta name="cover" content="cover-image">
        // but the actual cover image item has id="covera"): any image-typed manifest item whose id contains "cover".
        var coverByIdFuzzy = manifest.FirstOrDefault(m =>
            m.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && m.Id.Contains("cover", StringComparison.OrdinalIgnoreCase));
        return coverByIdFuzzy?.Href;
    }
}
