namespace iTextSharp.text.pdf;

/// <summary>
///     Cuts a structure tree down to the pages that are left after a page selection.
/// </summary>
/// <remarks>
///     <para>
///         SelectPages drops pages from the page tree and then collects unused objects, but every
///         structure element is still reachable from /StructTreeRoot, so none of them is unused and
///         none is collected. The elements describing the dropped pages stay behind, pointing at
///         content that is no longer in the document. Accessibility checkers report those as
///         orphaned tags, and every count in the tree still looks healthy while it describes a page
///         the reader will never reach.
///     </para>
///     <para>
///         The walk keeps a structure element only when something below it survives, and notes which
///         element owns each surviving marked content id so /ParentTree can be written fresh. Writing
///         it fresh is simpler than patching it: the keys have to be renumbered anyway once pages are
///         gone.
///     </para>
///     <para>
///         Structure elements that are direct rather than indirect objects cannot be named from
///         /ParentTree, so their marked content is dropped rather than left dangling. Annotations
///         keep their original /StructParent keys; only /OBJR entries pointing at a dropped page are
///         removed.
///     </para>
/// </remarks>
internal static class PdfStructureTreePruner
{
    /// <summary>
    ///     Removes everything in the structure tree that described a page the reader no longer has.
    ///     Does nothing to an untagged document.
    /// </summary>
    public static void Prune(PdfReader reader)
    {
        if (reader == null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        var structTreeRoot = reader.Catalog?.GetAsDict(PdfName.Structtreeroot);

        if (structTreeRoot == null)
        {
            return;
        }

        var survivors = new HashSet<int>();

        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            var pageRef = reader.GetPageOrigRef(page);

            if (pageRef != null)
            {
                survivors.Add(pageRef.Number);
            }
        }

        var owners = new Dictionary<int, Dictionary<int, PdfIndirectReference>>();
        var kept = new PdfArray();

        foreach (var kid in Children(structTreeRoot.Get(PdfName.K)))
        {
            if (KeepElement(kid, inheritedPage: null, survivors, owners))
            {
                kept.Add(kid);
            }
        }

        structTreeRoot.Put(PdfName.K, kept);
        RebuildParentTree(reader, structTreeRoot, owners);
    }

    /// <summary>
    ///     Decides one structure element, recursing into the elements below it. The element survives
    ///     when at least one of its children does.
    /// </summary>
    private static bool KeepElement(PdfObject kid, int? inheritedPage, HashSet<int> survivors,
        Dictionary<int, Dictionary<int, PdfIndirectReference>> owners)
    {
        if (PdfReader.GetPdfObject(kid) is not PdfDictionary element || element.Get(PdfName.S) == null)
        {
            return false;
        }

        var page = PageOf(element, inheritedPage);
        var reference = kid as PdfIndirectReference;
        var kept = new PdfArray();

        foreach (var child in Children(element.Get(PdfName.K)))
        {
            if (KeepChild(child, element: reference, page, survivors, owners))
            {
                kept.Add(child);
            }
        }

        if (kept.Size == 0)
        {
            return false;
        }

        element.Put(PdfName.K, kept);

        return true;
    }

    /// <summary>
    ///     A child of a structure element is a marked content id, a reference to one, a reference to
    ///     an object such as an annotation, or another structure element.
    /// </summary>
    private static bool KeepChild(PdfObject child, PdfIndirectReference element, int? page, HashSet<int> survivors,
        Dictionary<int, Dictionary<int, PdfIndirectReference>> owners)
    {
        var resolved = PdfReader.GetPdfObject(child);

        // A bare integer is a marked content id on the element's own page.
        if (resolved is PdfNumber mcid)
        {
            return Own(element, page, mcid.IntValue, survivors, owners);
        }

        if (resolved is not PdfDictionary dictionary)
        {
            return false;
        }

        var type = dictionary.Get(PdfName.TYPE);

        if (PdfName.Mcr.Equals(type))
        {
            var number = dictionary.GetAsNumber(PdfName.Mcid);

            return number != null &&
                   Own(element, PageOf(dictionary, page), number.IntValue, survivors, owners);
        }

        if (PdfName.Objr.Equals(type))
        {
            var objectPage = PageOf(dictionary, page);

            return objectPage.HasValue && survivors.Contains(objectPage.Value);
        }

        return KeepElement(child, page, survivors, owners);
    }

    /// <summary>
    ///     Records that <paramref name="element" /> owns a marked content id, which is what
    ///     /ParentTree has to say. An element written as a direct object cannot be referenced from
    ///     there, so its content is dropped rather than left unreachable.
    /// </summary>
    private static bool Own(PdfIndirectReference element, int? page, int mcid, HashSet<int> survivors,
        Dictionary<int, Dictionary<int, PdfIndirectReference>> owners)
    {
        if (element == null || !page.HasValue || !survivors.Contains(page.Value))
        {
            return false;
        }

        if (!owners.TryGetValue(page.Value, out var onPage))
        {
            onPage = new Dictionary<int, PdfIndirectReference>();
            owners[page.Value] = onPage;
        }

        onPage[mcid] = element;

        return true;
    }

    /// <summary>
    ///     Writes a fresh /ParentTree over the surviving pages and renumbers their /StructParents to
    ///     match. A page that kept no marked content loses /StructParents, which is what a page with
    ///     nothing to describe should look like.
    /// </summary>
    private static void RebuildParentTree(PdfReader reader, PdfDictionary structTreeRoot,
        Dictionary<int, Dictionary<int, PdfIndirectReference>> owners)
    {
        var nums = new PdfArray();
        var key = 0;

        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            var pageRef = reader.GetPageOrigRef(page);
            var pageDictionary = reader.GetPageN(page);

            if (pageDictionary == null)
            {
                continue;
            }

            if (pageRef == null || !owners.TryGetValue(pageRef.Number, out var onPage) || onPage.Count == 0)
            {
                pageDictionary.Remove(PdfName.Structparents);

                continue;
            }

            nums.Add(new PdfNumber(key));
            nums.Add(EntriesByMcid(onPage));
            pageDictionary.Put(PdfName.Structparents, new PdfNumber(key));
            key++;
        }

        var parentTree = structTreeRoot.GetAsDict(PdfName.Parenttree);

        if (parentTree == null)
        {
            parentTree = new PdfDictionary();
            structTreeRoot.Put(PdfName.Parenttree, parentTree);
        }

        // The tree that was there may have been branched into /Kids; a flat /Nums replaces either shape.
        parentTree.Remove(PdfName.Kids);
        parentTree.Remove(PdfName.Limits);
        parentTree.Put(PdfName.Nums, nums);
        structTreeRoot.Put(PdfName.Parenttreenextkey, new PdfNumber(key));
    }

    /// <summary>
    ///     The entry for a page is indexed by marked content id, so a gap left by a removed tag has
    ///     to be filled rather than skipped.
    /// </summary>
    private static PdfArray EntriesByMcid(Dictionary<int, PdfIndirectReference> onPage)
    {
        var highest = 0;

        foreach (var mcid in onPage.Keys)
        {
            if (mcid > highest)
            {
                highest = mcid;
            }
        }

        var entries = new PdfArray();

        for (var mcid = 0; mcid <= highest; mcid++)
        {
            entries.Add(onPage.TryGetValue(mcid, out var owner) ? owner : PdfNull.Pdfnull);
        }

        return entries;
    }

    private static int? PageOf(PdfDictionary element, int? inherited) =>
        element.Get(PdfName.Pg) is PrIndirectReference page ? page.Number : inherited;

    /// <summary>/K is one object, an array of them, or absent. This makes all three one shape.</summary>
    private static IList<PdfObject> Children(PdfObject kids)
    {
        if (kids == null)
        {
            return Array.Empty<PdfObject>();
        }

        if (kids is PdfArray direct)
        {
            return new List<PdfObject>(direct.ArrayList);
        }

        return PdfReader.GetPdfObject(kids) is PdfArray resolved
            ? new List<PdfObject>(resolved.ArrayList)
            : new List<PdfObject> { kids };
    }
}
