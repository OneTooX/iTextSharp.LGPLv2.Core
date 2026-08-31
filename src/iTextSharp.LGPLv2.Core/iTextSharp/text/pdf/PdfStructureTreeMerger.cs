namespace iTextSharp.text.pdf;

/// <summary>
///     Builds one structure tree for a document assembled out of several source documents.
/// </summary>
/// <remarks>
///     <para>
///         PdfCopy imports page content streams as they are, marked content operators included, but it
///         had no idea a structure tree existed. Everything it produced arrived with tags on the page
///         and nothing pointing at them: no /StructTreeRoot, no /MarkInfo, no /Lang. Tagged in the
///         stream, untagged to a reader.
///     </para>
///     <para>
///         Each copied page keeps its own marked content ids, because its content stream was copied
///         rather than rebuilt, so the elements can be rebuilt against the pages they came from and
///         /ParentTree keyed by the position of each page in the assembled document. Elements are
///         rebuilt rather than copied as objects: a structure element points at its parent as well as
///         its children, and following that would drag the whole source tree across.
///     </para>
///     <para>
///         An /OBJR names an annotation, and the assembled document holds a copy of it rather than the
///         annotation itself. PdfCopy knows what that copy became, so the reference is rebuilt against
///         it, and the key the annotation needs in /ParentTree is handed out before the page is copied,
///         since the copy carries whatever /StructParent it finds.
///     </para>
///     <para>
///         One thing is deliberately dropped: an element is kept only when a page it describes was
///         copied, so copying half a document leaves the other half's structure behind.
///     </para>
/// </remarks>
internal sealed class PdfStructureTreeMerger
{
    /// <summary>Keys worth carrying from a source element. /P, /K and /Pg are rebuilt instead.</summary>
    private static readonly PdfName[] _carried =
    {
        PdfName.S, PdfName.T, PdfName.Lang, PdfName.Alt, PdfName.E, PdfName.Actualtext, PdfName.A, PdfName.C,
        PdfName.Id
    };

    private readonly Dictionary<PdfReader, Dictionary<int, Annotation>> _annotations = new();
    private readonly Dictionary<int, TargetPage> _byMergedPage = new();
    private readonly List<TargetPage> _inOrder = new();
    private readonly Dictionary<PdfReader, Dictionary<int, TargetPage>> _pages = new();
    private readonly PdfDictionary _roleMap = new();
    private readonly List<PdfReader> _sources = new();
    private readonly Dictionary<PdfDictionary, bool> _survives = new();
    private readonly PdfWriter _writer;
    private PdfObject _lang;
    private int _nextKey;
    private PdfObject _title;
    private PdfDictionary _viewerPreferences;
    private byte[] _xmp;

    public PdfStructureTreeMerger(PdfWriter writer) => _writer = writer;

    /// <summary>True once at least one page of a tagged document has been copied.</summary>
    public bool HasStructure => _inOrder.Count > 0;

    /// <summary>The XMP packet the assembled document should carry, for the writer to adopt.</summary>
    public byte[] XmpMetadata => _xmp;

    /// <summary>
    ///     The title the assembled document should carry in its info dictionary. PDF/UA asks for a
    ///     title, and it is not always in the XMP - one of the OneTooX fixtures has an XMP packet a
    ///     reader cannot parse, and the title only survives here.
    /// </summary>
    public PdfObject Title => _title;

    /// <summary>
    ///     Notes where a source page ended up. Returns the /StructParents the copied page dictionary
    ///     should carry, or null when the source document is not tagged and the page needs none.
    /// </summary>
    public PdfNumber Track(PdfReader reader, PrIndirectReference sourcePage, PdfIndirectReference targetPage)
    {
        if (reader?.Catalog?.GetAsDict(PdfName.Structtreeroot) == null || sourcePage == null || targetPage == null)
        {
            return null;
        }

        if (!_pages.TryGetValue(reader, out var forReader))
        {
            forReader = new Dictionary<int, TargetPage>();
            _pages[reader] = forReader;
            _sources.Add(reader);
        }

        if (forReader.ContainsKey(sourcePage.Number))
        {
            return null; // the same source page copied twice; the first placement owns the structure
        }

        var target = new TargetPage(targetPage, _nextKey++);
        forReader[sourcePage.Number] = target;
        _inOrder.Add(target);

        return new PdfNumber(target.StructParents);
    }

    /// <summary>
    ///     Gives every tagged annotation on a source page the key it will have in the assembled
    ///     document, before the page is copied. The copy carries whatever /StructParent it finds, and is
    ///     written where it can no longer be reached, so the number has to be right beforehand - which
    ///     means writing it onto the source, whose own numbering the copy has left behind in any case.
    /// </summary>
    public void PrepareAnnotations(PdfReader reader, PdfDictionary page)
    {
        if (reader?.Catalog?.GetAsDict(PdfName.Structtreeroot) == null)
        {
            return;
        }

        var annots = page?.GetAsArray(PdfName.Annots);

        if (annots == null)
        {
            return;
        }

        if (!_annotations.TryGetValue(reader, out var forReader))
        {
            forReader = new Dictionary<int, Annotation>();
            _annotations[reader] = forReader;
        }

        for (var index = 0; index < annots.Size; index++)
        {
            if (annots[index] is not PrIndirectReference reference || forReader.ContainsKey(reference.Number)
                || PdfReader.GetPdfObject(reference) is not PdfDictionary annotation
                || annotation.Get(PdfName.Structparent) == null)
            {
                continue;
            }

            var noted = new Annotation(reference, _nextKey++);
            forReader[reference.Number] = noted;
            annotation.Put(PdfName.Structparent, new PdfNumber(noted.Key));
        }
    }

    /// <summary>
    ///     Notes what the annotations just copied became. The map PdfCopy keeps is per reader and
    ///     goes when the reader is freed, which a caller doing a long merge does as it proceeds, so the
    ///     reference has to be taken while the page is being copied rather than when the tree is built.
    /// </summary>
    public void NoteCopiedAnnotations(PdfReader reader, PdfCopy copy)
    {
        if (copy == null || reader == null || !_annotations.TryGetValue(reader, out var forReader))
        {
            return;
        }

        foreach (var noted in forReader.Values)
        {
            noted.Copied ??= copy.CopiedReference(reader, noted.Reference);
        }
    }

    /// <summary>
    ///     Writes the assembled tree and hangs it, and the catalog entries that go with it, off the
    ///     catalog of the document being written.
    /// </summary>
    public void Build(PdfDictionary catalog)
    {
        if (catalog == null || !HasStructure)
        {
            return;
        }

        AdoptMetadata();
        var rootReference = _writer.PdfIndirectReference;
        var root = new PdfDictionary(PdfName.Structtreeroot);
        var kids = new PdfArray();

        foreach (var reader in _sources)
        {
            var structTreeRoot = reader.Catalog.GetAsDict(PdfName.Structtreeroot);

            foreach (var kid in PdfStructureTreePruner.Children(structTreeRoot?.Get(PdfName.K)))
            {
                var copied = Rebuild(kid, reader, rootReference, inheritedPage: null);

                if (copied != null)
                {
                    kids.Add(copied);
                }
            }
        }

        if (kids.Size == 0)
        {
            return;
        }

        root.Put(PdfName.K, kids);
        root.Put(PdfName.Parenttree, BuildParentTree());
        root.Put(PdfName.Parenttreenextkey, new PdfNumber(_nextKey));

        if (_roleMap.Size > 0)
        {
            root.Put(PdfName.Rolemap, _roleMap);
        }

        _writer.AddToBody(root, rootReference);
        catalog.Put(PdfName.Structtreeroot, rootReference);

        var markInfo = new PdfDictionary();
        markInfo.Put(PdfName.Marked, PdfBoolean.Pdftrue);
        catalog.Put(PdfName.Markinfo, markInfo);

        if (_lang != null)
        {
            catalog.Put(PdfName.Lang, _lang);
        }

        if (_viewerPreferences != null && catalog.Get(PdfName.Viewerpreferences) == null)
        {
            catalog.Put(PdfName.Viewerpreferences, Duplicate(_viewerPreferences));
        }
    }

    /// <summary>
    ///     Rebuilds one element and everything under it, or returns null when nothing under it
    ///     describes a page that was copied.
    /// </summary>
    private PdfIndirectReference Rebuild(PdfObject node, PdfReader reader, PdfIndirectReference parent,
        int? inheritedPage)
    {
        if (PdfReader.GetPdfObject(node) is not PdfDictionary element)
        {
            return null;
        }

        var page = PageOf(element, inheritedPage);

        if (!Survives(element, reader, page))
        {
            return null;
        }

        var reference = _writer.PdfIndirectReference;
        var target = TargetOf(reader, page);
        var kids = new PdfArray();

        foreach (var child in PdfStructureTreePruner.Children(element.Get(PdfName.K)))
        {
            AddChild(child, reader, reference, page, target, kids);
        }

        if (kids.Size == 0)
        {
            return null;
        }

        var rebuilt = new PdfDictionary();

        foreach (var key in _carried)
        {
            var value = element.Get(key);

            if (value != null)
            {
                rebuilt.Put(key, Duplicate(value));
            }
        }

        rebuilt.Put(PdfName.K, kids);
        rebuilt.Put(PdfName.P, parent);

        if (target != null)
        {
            rebuilt.Put(PdfName.Pg, target.Reference);
        }

        _writer.AddToBody(rebuilt, reference);

        return reference;
    }

    private void AddChild(PdfObject child, PdfReader reader, PdfIndirectReference owner, int? page, TargetPage target,
        PdfArray kids)
    {
        var resolved = PdfReader.GetPdfObject(child);

        if (resolved is PdfNumber mcid)
        {
            if (target == null)
            {
                return;
            }

            kids.Add(mcid);
            target.Owners[mcid.IntValue] = owner;

            return;
        }

        if (resolved is not PdfDictionary dictionary)
        {
            return;
        }

        var type = dictionary.Get(PdfName.TYPE);

        if (PdfName.Mcr.Equals(type))
        {
            AddMarkedContentReference(dictionary, reader, owner, page, kids);

            return;
        }

        if (PdfName.Objr.Equals(type))
        {
            AddObjectReference(dictionary, reader, owner, page, kids);

            return;
        }

        var nested = Rebuild(child, reader, owner, page);

        if (nested != null)
        {
            kids.Add(nested);
        }
    }

    private void AddMarkedContentReference(PdfDictionary reference, PdfReader reader, PdfIndirectReference owner,
        int? page, PdfArray kids)
    {
        var mcid = reference.GetAsNumber(PdfName.Mcid);
        var target = TargetOf(reader, PageOf(reference, page));

        if (mcid == null || target == null)
        {
            return;
        }

        var rebuilt = new PdfDictionary(PdfName.Mcr);
        rebuilt.Put(PdfName.Mcid, new PdfNumber(mcid.IntValue));
        rebuilt.Put(PdfName.Pg, target.Reference);
        kids.Add(rebuilt);
        target.Owners[mcid.IntValue] = owner;
    }

    /// <summary>
    ///     An /OBJR names an annotation, and what the assembled document holds is a copy of it. The
    ///     reference is rebuilt against that copy, and the annotation's entry in /ParentTree is filled in
    ///     with the element holding it.
    /// </summary>
    private void AddObjectReference(PdfDictionary reference, PdfReader reader, PdfIndirectReference owner,
        int? page, PdfArray kids)
    {
        var noted = AnnotationOf(reference, reader);
        var target = TargetOf(reader, PageOf(reference, page));

        if (noted?.Copied == null || target == null)
        {
            return;
        }

        var rebuilt = new PdfDictionary(PdfName.Objr);
        rebuilt.Put(PdfName.Obj, noted.Copied);
        rebuilt.Put(PdfName.Pg, target.Reference);
        kids.Add(rebuilt);
        noted.Owner = owner;
    }

    /// <summary>The annotation an /OBJR names, when it is one this merge has keyed.</summary>
    private Annotation AnnotationOf(PdfDictionary reference, PdfReader reader)
    {
        if (reference.Get(PdfName.Obj) is not PrIndirectReference annotation
            || !_annotations.TryGetValue(reader, out var forReader))
        {
            return null;
        }

        return forReader.TryGetValue(annotation.Number, out var noted) ? noted : null;
    }

    /// <summary>
    ///     An element is worth rebuilding when it, or something under it, marks content on a page that
    ///     was copied. Answers are remembered because a wide tree asks about the same subtree twice.
    /// </summary>
    private bool Survives(PdfDictionary element, PdfReader reader, int? page)
    {
        if (element.Get(PdfName.S) == null)
        {
            return false;
        }

        if (_survives.TryGetValue(element, out var known))
        {
            return known;
        }

        _survives[element] = false; // stops a tree that points back at itself
        var survives = false;

        foreach (var child in PdfStructureTreePruner.Children(element.Get(PdfName.K)))
        {
            var resolved = PdfReader.GetPdfObject(child);

            survives = resolved switch
            {
                PdfNumber => TargetOf(reader, page) != null,
                PdfDictionary dictionary when PdfName.Mcr.Equals(dictionary.Get(PdfName.TYPE)) =>
                    TargetOf(reader, PageOf(dictionary, page)) != null,
                PdfDictionary dictionary when PdfName.Objr.Equals(dictionary.Get(PdfName.TYPE)) =>
                    AnnotationOf(dictionary, reader) != null && TargetOf(reader, PageOf(dictionary, page)) != null,
                PdfDictionary nested => Survives(nested, reader, PageOf(nested, page)),
                _ => false
            };

            if (survives)
            {
                break;
            }
        }

        _survives[element] = survives;

        return survives;
    }

    /// <summary>
    ///     /ParentTree names, for every page, the element owning each of its marked content ids. The
    ///     entry is indexed by id, so a page whose ids are not contiguous needs the gaps filled.
    /// </summary>
    private PdfDictionary BuildParentTree()
    {
        // Pages and annotations draw their keys from the same counter, so the entries are gathered by
        // key and written in that order: a number tree has to ascend.
        var entries = new SortedDictionary<int, PdfObject>();

        foreach (var page in _inOrder)
        {
            entries[page.StructParents] = EntriesByMcid(page);
        }

        foreach (var forReader in _annotations.Values)
        {
            foreach (var noted in forReader.Values)
            {
                if (noted.Owner != null)
                {
                    entries[noted.Key] = noted.Owner;
                }
            }
        }

        var nums = new PdfArray();

        foreach (var entry in entries)
        {
            nums.Add(new PdfNumber(entry.Key));
            nums.Add(entry.Value);
        }

        var parentTree = new PdfDictionary();
        parentTree.Put(PdfName.Nums, nums);

        return parentTree;
    }

    /// <summary>A page's entry is indexed by marked content id, so the gaps have to be filled.</summary>
    private static PdfArray EntriesByMcid(TargetPage page)
    {
        var entries = new PdfArray();
        var highest = -1;

        foreach (var mcid in page.Owners.Keys)
        {
            if (mcid > highest)
            {
                highest = mcid;
            }
        }

        for (var mcid = 0; mcid <= highest; mcid++)
        {
            entries.Add(page.Owners.TryGetValue(mcid, out var owner) ? owner : PdfNull.Pdfnull);
        }

        return entries;
    }

    /// <summary>
    ///     Decides what the assembled document says about itself. /Lang, the XMP packet and
    ///     /DisplayDocTitle describe one document, and there is no merging them, so they are taken
    ///     from the source that contributed the most pages - a letter with a cover page in front of it
    ///     is still the letter - and ties go to the one that came first. The role map is the exception:
    ///     it is a lookup, so it can genuinely be a union.
    /// </summary>
    private void AdoptMetadata()
    {
        PdfReader dominant = null;
        var most = 0;

        foreach (var reader in _sources)
        {
            if (_pages[reader].Count > most)
            {
                most = _pages[reader].Count;
                dominant = reader;
            }
        }

        if (dominant != null)
        {
            var catalog = dominant.Catalog;
            _lang = PdfReader.GetPdfObject(catalog.Get(PdfName.Lang));
            _viewerPreferences = catalog.GetAsDict(PdfName.Viewerpreferences);

            if (PdfReader.GetPdfObject(catalog.Get(PdfName.Metadata)) is PrStream metadata)
            {
                _xmp = PdfReader.GetStreamBytes(metadata);
            }

            var info = PdfReader.GetPdfObject(dominant.Trailer?.Get(PdfName.Info)) as PdfDictionary;
            _title = PdfReader.GetPdfObject(info?.Get(PdfName.Title));
        }

        foreach (var reader in _sources)
        {
            var roleMap = reader.Catalog.GetAsDict(PdfName.Structtreeroot)?.GetAsDict(PdfName.Rolemap);

            if (roleMap == null)
            {
                continue;
            }

            foreach (var role in roleMap.Keys)
            {
                if (_roleMap.Get(role) == null)
                {
                    _roleMap.Put(role, Duplicate(roleMap.Get(role)));
                }
            }
        }
    }

    private TargetPage TargetOf(PdfReader reader, int? page) =>
        page.HasValue && _pages.TryGetValue(reader, out var forReader) && forReader.TryGetValue(page.Value, out var target)
            ? target
            : null;

    private static int? PageOf(PdfDictionary element, int? inherited) =>
        element.Get(PdfName.Pg) is PrIndirectReference page ? page.Number : inherited;

    /// <summary>
    ///     Copies a value across as a direct object. Structure attributes are small, and resolving
    ///     them here keeps the assembled tree from referring back into a source document.
    /// </summary>
    private static PdfObject Duplicate(PdfObject value)
    {
        var resolved = PdfReader.GetPdfObject(value);

        switch (resolved)
        {
            case PdfArray array:
            {
                var copy = new PdfArray();

                foreach (var item in array.ArrayList)
                {
                    copy.Add(Duplicate(item));
                }

                return copy;
            }
            case PdfStream:
                return PdfNull.Pdfnull; // no structure attribute this library writes is a stream
            case PdfDictionary dictionary:
            {
                var copy = new PdfDictionary();

                foreach (var key in dictionary.Keys)
                {
                    copy.Put(key, Duplicate(dictionary.Get(key)));
                }

                return copy;
            }
            default:
                return resolved ?? PdfNull.Pdfnull;
        }
    }

    /// <summary>An annotation this merge has keyed, and the element that ends up holding it.</summary>
    private sealed class Annotation
    {
        public Annotation(PrIndirectReference reference, int key)
        {
            Reference = reference;
            Key = key;
        }

        public PrIndirectReference Reference { get; }
        public PdfIndirectReference Copied { get; set; }
        public int Key { get; }
        public PdfIndirectReference Owner { get; set; }
    }

    private sealed class TargetPage
    {
        public TargetPage(PdfIndirectReference reference, int structParents)
        {
            Reference = reference;
            StructParents = structParents;
        }

        public PdfIndirectReference Reference { get; }
        public int StructParents { get; }
        public Dictionary<int, PdfIndirectReference> Owners { get; } = new();
    }
}
