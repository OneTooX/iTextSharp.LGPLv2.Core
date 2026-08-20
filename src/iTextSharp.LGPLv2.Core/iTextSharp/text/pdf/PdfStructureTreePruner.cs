namespace iTextSharp.text.pdf;

/// <summary>
///     Cuts a structure tree down to the pages that are left after a page selection.
/// </summary>
/// <remarks>
///     <para>
///         SelectPages drops pages from the page tree and then collects unused objects, but every
///         structure element is still reachable from /StructTreeRoot, so none of them is unused and
///         none is collected. The elements describing the dropped pages stay behind, pointing at
///         content that is no longer in the document. Accessibility checkers report those as orphaned
///         tags, and every count in the tree still looks healthy while it describes a page the reader
///         will never reach.
///     </para>
///     <para>
///         An element is kept only when something below it still marks content on a page that
///         survived. /ParentTree is then derived again from what is left, by
///         <see cref="PdfParentTreeBuilder" />, rather than patched: the keys have to be renumbered
///         anyway once pages are gone.
///     </para>
///     <para>
///         /OBJR entries pointing at a dropped page are removed with the rest, but annotations that
///         survive keep their original /StructParent keys.
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

        var kept = new PdfArray();

        foreach (var kid in Children(structTreeRoot.Get(PdfName.K)))
        {
            if (KeepElement(kid, inheritedPage: null, survivors))
            {
                kept.Add(kid);
            }
        }

        structTreeRoot.Put(PdfName.K, kept);

        // Derived from what is left rather than patched, so the keys are renumbered along with it.
        PdfParentTreeBuilder.Rebuild(reader);
    }

    /// <summary>/K is one object, an array of them, or absent. This makes all three one shape.</summary>
    internal static IList<PdfObject> Children(PdfObject kids)
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

    /// <summary>
    ///     Decides one structure element, recursing into the elements below it. The element survives
    ///     when at least one of its children does.
    /// </summary>
    private static bool KeepElement(PdfObject kid, int? inheritedPage, HashSet<int> survivors)
    {
        if (PdfReader.GetPdfObject(kid) is not PdfDictionary element || element.Get(PdfName.S) == null)
        {
            return false;
        }

        var page = PageOf(element, inheritedPage);
        var kept = new PdfArray();

        foreach (var child in Children(element.Get(PdfName.K)))
        {
            if (KeepChild(child, page, survivors))
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
    private static bool KeepChild(PdfObject child, int? page, HashSet<int> survivors)
    {
        var resolved = PdfReader.GetPdfObject(child);

        if (resolved is PdfNumber)
        {
            return Lives(page, survivors);
        }

        if (resolved is not PdfDictionary dictionary)
        {
            return false;
        }

        var type = dictionary.Get(PdfName.TYPE);

        if (PdfName.Mcr.Equals(type))
        {
            return dictionary.GetAsNumber(PdfName.Mcid) != null && Lives(PageOf(dictionary, page), survivors);
        }

        if (PdfName.Objr.Equals(type))
        {
            return Lives(PageOf(dictionary, page), survivors);
        }

        return KeepElement(child, page, survivors);
    }

    private static bool Lives(int? page, HashSet<int> survivors) => page is int number && survivors.Contains(number);

    private static int? PageOf(PdfDictionary element, int? inherited) =>
        element.Get(PdfName.Pg) is PrIndirectReference page ? page.Number : inherited;
}
