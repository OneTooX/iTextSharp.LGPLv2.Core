namespace iTextSharp.text.pdf;

/// <summary>
///     Adds tagged content to a document that is already tagged, through a <see cref="PdfStamper" />.
/// </summary>
/// <remarks>
///     <para>
///         The library could already write a tagged document from scratch - <c>PdfWriter.SetTagged</c>
///         and <see cref="PdfStructureElement" /> - but that builds a tree of its own, which is no use
///         when stamping onto a document that arrived with one. Text stamped onto a page therefore
///         landed as bare content: visible, selectable, and invisible to a screen reader.
///     </para>
///     <para>
///         Each <see cref="Begin" /> opens a marked content sequence with the next free id on that
///         page and hangs a new structure element off the existing /StructTreeRoot.
///         <see cref="Complete" /> then rewrites /ParentTree so the reverse index matches, and has to
///         be called before the stamper is closed.
///     </para>
///     <para>
///         Only real content belongs here. Decoration - a line, a cross, an overlay - is an artifact
///         rather than a tag, and goes in <c>BeginMarkedContentSequence(new PdfName("Artifact"))</c>
///         instead.
///     </para>
/// </remarks>
public sealed class PdfStructureStamp
{
    private static readonly byte[] _mcidToken = { (byte)'/', (byte)'M', (byte)'C', (byte)'I', (byte)'D' };
    private static readonly PdfName _structElem = new(name: "StructElem");

    private readonly Dictionary<int, Dictionary<int, PdfIndirectReference>> _added = new();
    private readonly Dictionary<int, int> _nextMcid = new();
    private readonly PdfReader _reader;
    private readonly PdfIndirectReference _rootReference;
    private readonly PdfDictionary _structTreeRoot;
    private readonly PdfWriter _writer;

    public PdfStructureStamp(PdfStamper stamper)
    {
        if (stamper == null)
        {
            throw new ArgumentNullException(nameof(stamper));
        }

        _reader = stamper.Reader;
        _writer = stamper.Writer;
        _rootReference = _reader.Catalog?.Get(PdfName.Structtreeroot) as PrIndirectReference;
        _structTreeRoot = _reader.Catalog?.GetAsDict(PdfName.Structtreeroot);
    }

    /// <summary>
    ///     False when the document has no structure tree to add to, or has one that is not written as
    ///     an indirect object and so cannot be referred to. Stamping on such a document is untagged
    ///     either way, and <see cref="Begin" /> and <see cref="End" /> then do nothing.
    /// </summary>
    public bool IsTagged => _structTreeRoot != null && _rootReference != null;

    /// <summary>
    ///     Opens a marked content sequence on <paramref name="content" /> and gives it a structure
    ///     element of its own. Everything drawn until <see cref="End" /> belongs to it.
    /// </summary>
    /// <param name="content">the page content to write to, from <c>PdfStamper.GetOverContent</c></param>
    /// <param name="page">the page number the content is going on, one based</param>
    /// <param name="role">the structure type, for instance <c>PdfName.P</c></param>
    /// <param name="readFirst">
    ///     Where the element belongs in reading order. Appended by default; pass true for content
    ///     that is read before what the document already says, such as an address in the envelope
    ///     window at the top of the first page. The library cannot work this out - it knows where the
    ///     content is on the page, not what it means - so the caller has to say.
    /// </param>
    public void Begin(PdfContentByte content, int page, PdfName role, bool readFirst = false)
    {
        if (content == null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (role == null)
        {
            throw new ArgumentNullException(nameof(role));
        }

        if (!IsTagged)
        {
            return;
        }

        var mcid = NextMcid(page);
        var container = Container(out var containerReference);
        var element = new PdfDictionary(_structElem);
        element.Put(PdfName.S, role);
        element.Put(PdfName.P, containerReference);
        element.Put(PdfName.Pg, _reader.GetPageOrigRef(page));
        element.Put(PdfName.K, new PdfNumber(mcid));
        var reference = _writer.AddToBody(element).IndirectReference;
        Attach(container, reference, readFirst);
        Remember(page, mcid, reference);

        var properties = new PdfDictionary();
        properties.Put(PdfName.Mcid, new PdfNumber(mcid));
        content.BeginMarkedContentSequence(role, properties, inline: true);
    }

    /// <summary>Closes the sequence <see cref="Begin" /> opened.</summary>
    public void End(PdfContentByte content)
    {
        if (content == null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (IsTagged)
        {
            content.EndMarkedContentSequence();
        }
    }

    /// <summary>
    ///     Brings /ParentTree back in step with the tree. Call before closing the stamper; calling it
    ///     when nothing was tagged is harmless.
    /// </summary>
    public void Complete()
    {
        if (IsTagged)
        {
            PdfParentTreeBuilder.Rebuild(_reader, _added);
        }
    }

    /// <summary>
    ///     Notes the element for /ParentTree. Walking the tree will not find it: the reference belongs
    ///     to the writer, and the reader cannot resolve one of those.
    /// </summary>
    private void Remember(int page, int mcid, PdfIndirectReference element)
    {
        var pageNumber = _reader.GetPageOrigRef(page).Number;

        if (!_added.TryGetValue(pageNumber, out var onPage))
        {
            onPage = new Dictionary<int, PdfIndirectReference>();
            _added[pageNumber] = onPage;
        }

        onPage[mcid] = element;
    }

    /// <summary>
    ///     Where a new element belongs. A tagged document normally hangs everything off one element
    ///     below the root - /Document, as a rule - and adding a sibling of that rather than a child
    ///     leaves the page content in two trees that no longer say which comes first. So the single
    ///     top level element is the container when there is one, and the root itself otherwise.
    /// </summary>
    private PdfDictionary Container(out PdfIndirectReference containerReference)
    {
        var kids = PdfStructureTreePruner.Children(_structTreeRoot.Get(PdfName.K));

        if (kids.Count == 1 && kids[0] is PrIndirectReference reference &&
            PdfReader.GetPdfObject(reference) is PdfDictionary only && only.Get(PdfName.S) != null)
        {
            containerReference = reference;

            return only;
        }

        containerReference = _rootReference;

        return _structTreeRoot;
    }

    /// <summary>Hangs a new element off the container, turning a single kid into an array if need be.</summary>
    private static void Attach(PdfDictionary container, PdfIndirectReference element, bool readFirst)
    {
        if (PdfReader.GetPdfObject(container.Get(PdfName.K)) is PdfArray array)
        {
            if (readFirst)
            {
                array.Add(index: 0, element);
            }
            else
            {
                array.Add(element);
            }

            return;
        }

        var replacement = new PdfArray();
        var existing = container.Get(PdfName.K);

        if (readFirst)
        {
            replacement.Add(element);
            if (existing != null) replacement.Add(existing);
        }
        else
        {
            if (existing != null) replacement.Add(existing);
            replacement.Add(element);
        }

        container.Put(PdfName.K, replacement);
    }

    /// <summary>
    ///     Marked content ids are per page and per content stream, so a new one has to clear whatever
    ///     the page already uses. The page is read once and the count carried from there.
    /// </summary>
    private int NextMcid(int page)
    {
        if (!_nextMcid.TryGetValue(page, out var next))
        {
            next = HighestMcid(_reader.GetPageContent(page)) + 1;
        }

        _nextMcid[page] = next + 1;

        return next;
    }

    private static int HighestMcid(byte[] content)
    {
        var highest = -1;

        if (content == null)
        {
            return highest;
        }

        for (var index = 0; index + _mcidToken.Length < content.Length; index++)
        {
            if (!Matches(content, index))
            {
                continue;
            }

            var cursor = index + _mcidToken.Length;

            while (cursor < content.Length && content[cursor] == ' ')
            {
                cursor++;
            }

            var value = -1;

            while (cursor < content.Length && content[cursor] >= '0' && content[cursor] <= '9')
            {
                value = (value < 0 ? 0 : value * 10) + (content[cursor] - '0');
                cursor++;
            }

            if (value > highest)
            {
                highest = value;
            }
        }

        return highest;
    }

    private static bool Matches(byte[] content, int index)
    {
        for (var offset = 0; offset < _mcidToken.Length; offset++)
        {
            if (content[index + offset] != _mcidToken[offset])
            {
                return false;
            }
        }

        return true;
    }
}
