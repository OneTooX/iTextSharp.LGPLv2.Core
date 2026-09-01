using System.util;

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
///         Where the element lands in the tree is the reading order, and appending is almost always
///         wrong: content stamped into the top of the first page - a letterhead, an address, a
///         subject line - was announced after the whole document. So <see cref="Begin" /> takes the
///         y the content is drawn at and places the element among the ones already there, page
///         ascending and then top to bottom, using <see cref="PdfContentPositions" /> to find where
///         those sit on their pages. That is an assumption about the document: a single column read
///         downwards. It holds for letters, and it is the only ordering derivable from the page
///         itself. Callers that pass no y still append.
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
    private static readonly PdfName _mcr = new(name: "MCR");
    private static readonly PdfName _objr = new(name: "OBJR");

    private readonly Dictionary<int, Dictionary<int, PdfIndirectReference>> _added = new();
    private readonly Dictionary<PdfDictionary, PdfIndirectReference> _addedAnnotations = new();
    private readonly Dictionary<int, int> _nextMcid = new();
    private readonly Dictionary<int, int> _pageByObjectNumber = new();
    private readonly List<Placement> _placements = new();
    private readonly Dictionary<int, Dictionary<int, float>> _positions = new();
    private readonly PdfReader _reader;
    private readonly PdfIndirectReference _rootReference;
    private readonly PdfDictionary _structTreeRoot;
    private readonly PdfWriter _writer;
    private PdfArray _containerKids;
    private PdfIndirectReference _containerReference;

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
    /// <param name="top">
    ///     The y the content is drawn at, in default user space, which is what places the element in
    ///     reading order. Leave it out only when the position is genuinely unknown; the element is
    ///     then appended, and is read after everything the document already says.
    /// </param>
    public void Begin(PdfContentByte content, int page, PdfName role, float? top = null)
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
        EnsureContainer();
        var element = new PdfDictionary(_structElem);
        element.Put(PdfName.S, role);
        element.Put(PdfName.P, _containerReference);
        element.Put(PdfName.Pg, _reader.GetPageOrigRef(page));
        element.Put(PdfName.K, new PdfNumber(mcid));
        var reference = _writer.AddToBody(element).IndirectReference;
        Attach(reference, top.HasValue ? new Placement(page, top.Value) : Placement.Last);
        Remember(page, mcid, reference);

        var properties = new PdfDictionary();
        properties.Put(PdfName.Mcid, new PdfNumber(mcid));
        content.BeginMarkedContentSequence(role, properties, inline: true);
    }

    /// <summary>
    ///     Gives an annotation a structure element of its own, holding a reference to the annotation
    ///     rather than to marked content.
    /// </summary>
    /// <remarks>
    ///     A link or a form field is reachable when the tree holds an /OBJR naming it and the annotation
    ///     carries the /StructParent that names the element back. Neither exists for an annotation the
    ///     caller has just made, so both are written here: the element now, and the key when
    ///     <see cref="Complete" /> settles the numbering.
    ///     <para>
    ///         The element is hung in a paragraph rather than straight off the container. A Link is an
    ///         inline-level element and belongs inside a block-level one; a checker reading a Link as a
    ///         direct child of the document element reports it as inappropriate use, and it is.
    ///     </para>
    /// </remarks>
    /// <param name="page">the page the annotation sits on, one based</param>
    /// <param name="annotation">the annotation dictionary, still to be written to the body</param>
    /// <param name="reference">the reference it will be written at</param>
    /// <param name="role">the structure type, for instance <c>PdfName.Link</c></param>
    /// <param name="top">the y it sits at, which is what places it in reading order</param>
    /// <param name="alternate">
    ///     what the annotation says, for an element that holds no text of its own. An element with
    ///     nothing but an /OBJR under it is announced as a link with no name, so the caller passes
    ///     whatever the annotation covers.
    /// </param>
    public void AddAnnotation(int page, PdfDictionary annotation, PdfIndirectReference reference,
        PdfName role, float? top = null, string alternate = null)
    {
        if (annotation == null)
        {
            throw new ArgumentNullException(nameof(annotation));
        }

        if (reference == null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (role == null)
        {
            throw new ArgumentNullException(nameof(role));
        }

        if (!IsTagged)
        {
            return;
        }

        EnsureContainer();
        var pageReference = _reader.GetPageOrigRef(page);
        var wrapperReference = _writer.PdfIndirectReference;
        var element = new PdfDictionary(_structElem);
        element.Put(PdfName.S, role);
        element.Put(PdfName.P, wrapperReference);
        element.Put(PdfName.Pg, pageReference);

        if (!string.IsNullOrEmpty(alternate))
        {
            element.Put(PdfName.Alt, new PdfString(alternate, PdfObject.TEXT_UNICODE));
        }

        var objectReference = new PdfDictionary(_objr);
        objectReference.Put(PdfName.Obj, reference);
        objectReference.Put(PdfName.Pg, pageReference);
        element.Put(PdfName.K, objectReference);

        var elementReference = _writer.AddToBody(element).IndirectReference;

        var wrapper = new PdfDictionary(_structElem);
        wrapper.Put(PdfName.S, PdfName.P);
        wrapper.Put(PdfName.P, _containerReference);
        wrapper.Put(PdfName.Pg, pageReference);
        wrapper.Put(PdfName.K, new PdfArray(elementReference));
        _writer.AddToBody(wrapper, wrapperReference);

        Attach(wrapperReference, top.HasValue ? new Placement(page, top.Value) : Placement.Last);
        _addedAnnotations[annotation] = elementReference;
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
            PdfParentTreeBuilder.Rebuild(_reader, _added, _addedAnnotations);
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
    ///     Resolves the container once and works out where everything already in it sits on the page.
    /// </summary>
    /// <remarks>
    ///     A tagged document normally hangs everything off one element below the root - /Document, as
    ///     a rule - and adding a sibling of that rather than a child leaves the page content in two
    ///     trees that no longer say which comes first. So the single top level element is the
    ///     container when there is one, and the root itself otherwise. Its /K is normalised to an
    ///     array here so that later insertions are a matter of index.
    /// </remarks>
    private void EnsureContainer()
    {
        if (_containerKids != null)
        {
            return;
        }

        var kids = PdfStructureTreePruner.Children(_structTreeRoot.Get(PdfName.K));
        PdfDictionary container;

        if (kids.Count == 1 && kids[0] is PrIndirectReference reference &&
            PdfReader.GetPdfObject(reference) is PdfDictionary only && only.Get(PdfName.S) != null)
        {
            container = only;
            _containerReference = reference;
        }
        else
        {
            container = _structTreeRoot;
            _containerReference = _rootReference;
        }

        for (var page = 1; page <= _reader.NumberOfPages; page++)
        {
            var pageReference = _reader.GetPageOrigRef(page);

            if (pageReference != null)
            {
                _pageByObjectNumber[pageReference.Number] = page;
            }
        }

        _containerKids = PdfReader.GetPdfObject(container.Get(PdfName.K)) as PdfArray;

        if (_containerKids == null)
        {
            _containerKids = new PdfArray();
            var existing = container.Get(PdfName.K);

            if (existing != null)
            {
                _containerKids.Add(existing);
            }

            container.Put(PdfName.K, _containerKids);
        }

        var previous = Placement.First;

        foreach (var kid in _containerKids.ArrayList)
        {
            previous = PlacementOf(kid, previous);
            _placements.Add(previous);
        }
    }

    /// <summary>
    ///     Hangs the element off the container at the point <paramref name="placement" /> asks for.
    ///     An element that could not be placed keeps the position of the one before it, so a tree
    ///     this cannot read stays in the order it arrived in.
    /// </summary>
    private void Attach(PdfIndirectReference element, Placement placement)
    {
        var index = _containerKids.Size;

        for (var candidate = 0; candidate < _placements.Count; candidate++)
        {
            if (_placements[candidate].CompareTo(placement) > 0)
            {
                index = candidate;

                break;
            }
        }

        _containerKids.Add(index, element);
        _placements.Insert(index, placement);
    }

    /// <summary>
    ///     Where a kid of the container starts on its page: the first page and marked content id
    ///     found below it, looked up in that page's content. Falls back to
    ///     <paramref name="previous" /> when there is nothing to go on - an element drawing only
    ///     inside a form XObject, say - which keeps it next to its neighbour.
    /// </summary>
    private Placement PlacementOf(PdfObject kid, Placement previous)
    {
        var page = 0;
        var mcid = -1;

        if (!Locate(kid, ref page, ref mcid, depth: 0) || page == 0)
        {
            return previous;
        }

        return PositionsFor(page).TryGetValue(mcid, out var y) ? new Placement(page, y) : previous;
    }

    /// <summary>
    ///     Depth first search for the first marked content the element covers, and the page it is on.
    ///     Both /K forms are followed: a bare id, which takes its page from the nearest /Pg above it,
    ///     and an /MCR, which carries its own.
    /// </summary>
    private bool Locate(PdfObject node, ref int page, ref int mcid, int depth)
    {
        // The tree is a tree, but a damaged one need not be, and this must not be what hangs.
        if (depth > 64)
        {
            return false;
        }

        var resolved = PdfReader.GetPdfObject(node);

        if (resolved is PdfNumber number)
        {
            mcid = number.IntValue;

            return page != 0;
        }

        if (resolved is not PdfDictionary dictionary)
        {
            return false;
        }

        if (dictionary.Get(PdfName.Pg) is PrIndirectReference pageReference &&
            _pageByObjectNumber.TryGetValue(pageReference.Number, out var pageNumber))
        {
            page = pageNumber;
        }

        var type = dictionary.Get(PdfName.TYPE);

        if (_objr.Equals(type))
        {
            return false;
        }

        if (_mcr.Equals(type))
        {
            if (dictionary.Get(PdfName.Mcid) is not PdfNumber id)
            {
                return false;
            }

            mcid = id.IntValue;

            return page != 0;
        }

        foreach (var child in PdfStructureTreePruner.Children(dictionary.Get(PdfName.K)))
        {
            var childPage = page;

            if (Locate(child, ref childPage, ref mcid, depth + 1))
            {
                page = childPage;

                return true;
            }
        }

        return false;
    }

    private Dictionary<int, float> PositionsFor(int page)
    {
        if (!_positions.TryGetValue(page, out var onPage))
        {
            onPage = PdfContentPositions.FirstY(_reader.GetPageContent(page));
            _positions[page] = onPage;
        }

        return onPage;
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

    /// <summary>A point in reading order: which page, and how far down it.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private readonly struct Placement : IComparable<Placement>, IEquatable<Placement>
    {
        public static readonly Placement First = new(page: 0, float.MaxValue);
        public static readonly Placement Last = new(int.MaxValue, float.MinValue);

        private readonly int _page;
        private readonly float _y;

        public Placement(int page, float y)
        {
            _page = page;
            _y = y;
        }

        /// <summary>Pages in order, and within a page the higher up the earlier.</summary>
        public int CompareTo(Placement other) =>
            _page != other._page ? _page.CompareTo(other._page) : other._y.CompareTo(_y);

        public bool Equals(Placement other) => _page == other._page && _y.ApproxEquals(other._y);

        public override bool Equals(object obj) => obj is Placement other && Equals(other);

        public override int GetHashCode() => _page;
    }
}
