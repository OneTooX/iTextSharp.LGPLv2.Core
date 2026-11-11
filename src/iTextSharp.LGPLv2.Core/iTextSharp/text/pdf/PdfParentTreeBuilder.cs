namespace iTextSharp.text.pdf;

/// <summary>
///     Derives /ParentTree and every page's /StructParents from the structure tree itself.
/// </summary>
/// <remarks>
///     <para>
///         /ParentTree is the reverse of the tree: for each page it names, per marked content id, the
///         element that owns it. Nothing keeps the two sides in step, so any edit that changes which
///         pages exist or what is tagged on them leaves the reverse index describing a document that
///         is no longer there. Rewriting it from the tree is both simpler and safer than patching it,
///         because the keys have to be renumbered anyway once pages have moved.
///     </para>
///     <para>
///         Annotations key into the same number tree through their own /StructParent, and their entries
///         are derived along with the pages': the element holding an annotation's /OBJR is what its key
///         names, written directly rather than through an array, and the key is written back onto the
///         annotation, the one it arrived with belonging to a numbering that is now gone. An annotation
///         the tree does not point at keeps the key it had, there being nothing to renumber it to.
///     </para>
/// </remarks>
internal static class PdfParentTreeBuilder
{
    /// <param name="reader">the document whose tree is the truth</param>
    /// <param name="added">
    ///     Elements written through the writer rather than read from the document, by page object
    ///     number and marked content id. The walk cannot find these: their references belong to the
    ///     writer, and asking the reader to resolve one gets nothing back.
    /// </param>
    /// <param name="addedAnnotations">
    ///     Annotations written through the writer, by the element that holds them. The walk cannot
    ///     find these for the same reason, and the key goes onto the annotation itself, which the
    ///     caller still has in hand.
    /// </param>
    public static void Rebuild(PdfReader reader,
        Dictionary<int, Dictionary<int, PdfIndirectReference>> added = null,
        Dictionary<PdfDictionary, PdfIndirectReference> addedAnnotations = null)
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

        var owners = new Dictionary<int, Dictionary<int, PdfIndirectReference>>();
        var annotations = new Dictionary<int, PdfIndirectReference>();

        foreach (var kid in PdfStructureTreePruner.Children(structTreeRoot.Get(PdfName.K)))
        {
            Collect(kid, inheritedPage: null, owners, annotations);
        }

        if (added != null)
        {
            foreach (var page in added)
            {
                foreach (var owned in page.Value)
                {
                    Own(owners, page.Key, owned.Key, owned.Value);
                }
            }
        }

        Write(reader, structTreeRoot, owners, annotations, addedAnnotations);
    }

    /// <summary>Walks the tree noting which element owns each marked content id on each page.</summary>
    private static void Collect(PdfObject node, int? inheritedPage,
        Dictionary<int, Dictionary<int, PdfIndirectReference>> owners,
        Dictionary<int, PdfIndirectReference> annotations)
    {
        if (PdfReader.GetPdfObject(node) is not PdfDictionary element || element.Get(PdfName.S) == null)
        {
            return;
        }

        var page = PageOf(element, inheritedPage);
        var reference = node as PdfIndirectReference;

        foreach (var child in PdfStructureTreePruner.Children(element.Get(PdfName.K)))
        {
            var resolved = PdfReader.GetPdfObject(child);

            if (resolved is PdfNumber mcid)
            {
                Own(owners, page, mcid.IntValue, reference);

                continue;
            }

            if (resolved is not PdfDictionary dictionary)
            {
                continue;
            }

            var type = dictionary.Get(PdfName.TYPE);

            if (PdfName.Mcr.Equals(type))
            {
                var number = dictionary.GetAsNumber(PdfName.Mcid);

                if (number != null)
                {
                    Own(owners, PageOf(dictionary, page), number.IntValue, reference);
                }

                continue;
            }

            if (PdfName.Objr.Equals(type))
            {
                // The annotation keys into this tree itself, and what its key has to name is the
                // element that holds the reference to it.
                if (dictionary.Get(PdfName.Obj) is PrIndirectReference annotation && reference != null)
                {
                    annotations[annotation.Number] = reference;
                }

                continue;
            }

            Collect(child, page, owners, annotations);
        }
    }

    private static void Own(Dictionary<int, Dictionary<int, PdfIndirectReference>> owners, int? page, int mcid,
        PdfIndirectReference element)
    {
        // An element written as a direct object cannot be named from /ParentTree, so its content is
        // left out rather than pointed at with something a reader cannot follow.
        if (element == null || !page.HasValue)
        {
            return;
        }

        if (!owners.TryGetValue(page.Value, out var onPage))
        {
            onPage = new Dictionary<int, PdfIndirectReference>();
            owners[page.Value] = onPage;
        }

        onPage[mcid] = element;
    }

    private static void Write(PdfReader reader, PdfDictionary structTreeRoot,
        Dictionary<int, Dictionary<int, PdfIndirectReference>> owners,
        Dictionary<int, PdfIndirectReference> annotations,
        Dictionary<PdfDictionary, PdfIndirectReference> addedAnnotations)
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

        key = WriteAnnotations(reader, nums, annotations, key);
        key = WriteAddedAnnotations(nums, addedAnnotations, key);

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
    ///     Writes one entry per annotation the tree points at, after the pages' entries. The value is
    ///     the element itself rather than an array - ISO 32000-1 14.7.4.4 - and the key goes back onto
    ///     the annotation, since the one it arrived with belonged to a numbering that is now gone.
    /// </summary>
    private static int WriteAnnotations(PdfReader reader, PdfArray nums,
        Dictionary<int, PdfIndirectReference> annotations, int key)
    {
        if (annotations.Count == 0)
        {
            return key;
        }

        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            var annots = reader.GetPageN(page)?.GetAsArray(PdfName.Annots);

            if (annots == null)
            {
                continue;
            }

            for (var index = 0; index < annots.Size; index++)
            {
                if (annots[index] is not PrIndirectReference annotation
                    || !annotations.TryGetValue(annotation.Number, out var owner)
                    || PdfReader.GetPdfObject(annotation) is not PdfDictionary dictionary)
                {
                    continue;
                }

                nums.Add(new PdfNumber(key));
                nums.Add(owner);
                dictionary.Put(PdfName.Structparent, new PdfNumber(key));
                key++;
            }
        }

        return key;
    }

    /// <summary>
    ///     A page's entry is indexed by marked content id, so a gap - left by a removed tag, or by
    ///     content that was never tagged - has to be filled rather than skipped.
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

    /// <summary>
    ///     Writes the entries for annotations the writer holds. They are not on a page the reader can
    ///     be asked about yet, so the key is put straight onto the dictionary the caller is still
    ///     holding, before it goes to the body.
    /// </summary>
    private static int WriteAddedAnnotations(PdfArray nums,
        Dictionary<PdfDictionary, PdfIndirectReference> annotations, int key)
    {
        if (annotations == null)
        {
            return key;
        }

        foreach (var annotation in annotations)
        {
            nums.Add(new PdfNumber(key));
            nums.Add(annotation.Value);
            annotation.Key.Put(PdfName.Structparent, new PdfNumber(key));
            key++;
        }

        return key;
    }

    private static int? PageOf(PdfDictionary element, int? inherited) =>
        element.Get(PdfName.Pg) is PrIndirectReference page ? page.Number : inherited;
}
