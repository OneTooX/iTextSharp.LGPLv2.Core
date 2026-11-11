namespace iTextSharp.text.pdf;

/// <summary>
///     Moves the marked content that draws an image into an artifact, and takes its structure element
///     with it.
/// </summary>
/// <remarks>
///     <para>
///         A caller that replaces an image with a blank one - a print code read off the page and then
///         hidden, say - leaves the tag behind: an element still pointing at content that no longer
///         says anything. PDF/UA has a place for content like that, and it is not the structure tree.
///         A production marking is an artifact, so that a reader passes over it instead of announcing
///         something that is no longer there.
///     </para>
///     <para>
///         The sequence is retagged where it is written, so the page goes on drawing what it drew, and
///         the element is taken out of the tree along with any ancestor left holding nothing.
///         /ParentTree is derived again afterwards by <see cref="PdfParentTreeBuilder" />, since what
///         it indexes has changed.
///     </para>
///     <para>
///         Only the page's own content stream is read. An image drawn from inside a form XObject is
///         not reached, and a sequence whose /MCID cannot be read - an indirect property list, or a
///         BMC with no id at all - is left alone: there is no element to take out, and retagging one
///         half of the pair would leave the tree naming content that no longer carries the id.
///     </para>
/// </remarks>
public static class PdfContentArtifacts
{
    private static readonly byte[] Artifact = DocWriter.GetIsoBytes(" /Artifact BMC");

    private static readonly PdfName ArtifactTag = new(name: "Artifact");

    /// <summary>
    ///     Retags every marked content sequence that draws one of the given XObjects as an artifact,
    ///     and removes the structure elements that described them.
    /// </summary>
    /// <param name="reader">the document to edit, before it is written out</param>
    /// <param name="xobjects">
    ///     the images, as the references a page's /Resources holds. Anything that is not an indirect
    ///     reference is ignored, an inline image having no name to be drawn by.
    /// </param>
    /// <returns>the number of sequences retagged</returns>
    public static int MarkXObjectsAsArtifacts(PdfReader reader, ICollection<PdfObject> xobjects)
    {
        if (reader == null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        if (xobjects == null)
        {
            throw new ArgumentNullException(nameof(xobjects));
        }

        var wanted = new HashSet<int>();

        foreach (var xobject in xobjects)
        {
            if (xobject is PrIndirectReference reference)
            {
                wanted.Add(reference.Number);
            }
        }

        if (wanted.Count == 0)
        {
            return 0;
        }

        var retagged = 0;
        var orphaned = new HashSet<long>();

        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            var names = NamesOf(reader.GetPageN(page), wanted);

            if (names.Count == 0)
            {
                continue;
            }

            var content = reader.GetPageContent(page);
            var sequences = Sequences(content, names);

            if (sequences.Count == 0)
            {
                continue;
            }

            reader.SetPageContent(page, Retag(content, sequences));
            var pageRef = reader.GetPageOrigRef(page);

            if (pageRef != null)
            {
                foreach (var sequence in sequences)
                {
                    orphaned.Add(Key(pageRef.Number, sequence.Mcid));
                }
            }

            retagged += sequences.Count;
        }

        if (retagged == 0)
        {
            return 0;
        }

        Remove(reader, orphaned);
        PdfParentTreeBuilder.Rebuild(reader);

        return retagged;
    }

    /// <summary>The names a page draws the given objects by. The reader pushes inherited resources onto the page.</summary>
    private static HashSet<PdfName> NamesOf(PdfDictionary page, HashSet<int> wanted)
    {
        var names = new HashSet<PdfName>();
        var resources = PdfReader.GetPdfObject(page?.Get(PdfName.Resources)) as PdfDictionary;

        if (PdfReader.GetPdfObject(resources?.Get(PdfName.Xobject)) is not PdfDictionary xobjects)
        {
            return names;
        }

        foreach (var name in xobjects.Keys)
        {
            if (xobjects.Get(name) is PrIndirectReference reference && wanted.Contains(reference.Number))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    ///     The sequences that draw one of the named XObjects, innermost first: a figure inside a
    ///     paragraph is the figure's own tag to take out, not the paragraph's.
    /// </summary>
    private static List<Sequence> Sequences(byte[] content, HashSet<PdfName> names)
    {
        var found = new List<Sequence>();
        var open = new Stack<Sequence>();
        var parser = new PdfContentParser(new PrTokeniser(new RandomAccessFileOrArray(content)));
        var operands = new List<PdfObject>();

        try
        {
            while (true)
            {
                // Where this operator's operands begin. The span reaches back to the end of the one
                // before, so replacing it takes the whitespace in front along with it.
                var start = parser.Tokeniser.FilePointer;

                if (parser.Parse(operands).Count == 0)
                {
                    break;
                }

                var end = parser.Tokeniser.FilePointer;

                switch (operands[operands.Count - 1].ToString())
                {
                    case "BMC":
                    case "BDC":
                        open.Push(new Sequence(start, end, MarkedContentId(operands), IsArtifact(operands)));

                        break;
                    case "EMC":
                        if (open.Count > 0)
                        {
                            open.Pop();
                        }

                        break;
                    case "ID":
                        SkipInlineImageData(parser, content);

                        break;
                    case "Do":
                        var sequence = open.Count > 0 ? open.Peek() : null;

                        if (sequence == null || sequence.IsArtifact || sequence.Mcid < 0)
                        {
                            break;
                        }

                        if (operands.Count > 1 && operands[operands.Count - 2] is PdfName drawn && names.Contains(drawn)
                            && !found.Contains(sequence))
                        {
                            found.Add(sequence);
                        }

                        break;
                }
            }
        }
        catch (Exception)
        {
            // A stream that cannot be read through is left alone rather than half edited. The print
            // code stays tagged, which is where it was before being asked about.
            return new List<Sequence>();
        }

        found.Sort((left, right) => left.Start.CompareTo(right.Start));

        return found;
    }

    /// <summary>
    ///     Steps over the bytes of an inline image. They are not tokens, and lexing them as operators
    ///     ends the scan on the first byte that is not one.
    /// </summary>
    private static void SkipInlineImageData(PdfContentParser parser, byte[] content)
    {
        // One whitespace byte separates ID from the data, and belongs to the data's delimiter.
        var position = parser.Tokeniser.FilePointer + 1;

        while (position + 1 < content.Length)
        {
            if (content[position] == 'E' && content[position + 1] == 'I'
                && PrTokeniser.IsWhitespace(content[position - 1])
                && (position + 2 >= content.Length || PrTokeniser.IsWhitespace(content[position + 2])))
            {
                parser.Tokeniser.Seek(position + 2);

                return;
            }

            position++;
        }

        parser.Tokeniser.Seek(content.Length);
    }

    private static byte[] Retag(byte[] content, List<Sequence> sequences)
    {
        using var output = new MemoryStream(content.Length);
        var copied = 0;

        foreach (var sequence in sequences)
        {
            output.Write(content, copied, sequence.Start - copied);
            output.Write(Artifact, offset: 0, Artifact.Length);
            copied = sequence.End;
        }

        output.Write(content, copied, content.Length - copied);

        return output.ToArray();
    }

    /// <summary>The tag of a BMC or BDC, which is the first operand.</summary>
    private static bool IsArtifact(List<PdfObject> operands) =>
        operands.Count > 0 && ArtifactTag.Equals(operands[index: 0]);

    /// <summary>The /MCID of a BDC, or -1 when the sequence carries no id or an indirect property list.</summary>
    private static int MarkedContentId(List<PdfObject> operands)
    {
        if (operands.Count < 3 || operands[operands.Count - 2] is not PdfDictionary properties)
        {
            return -1;
        }

        return properties.Get(PdfName.Mcid) is PdfNumber mcid ? mcid.IntValue : -1;
    }

    /// <summary>Takes the retagged ids out of the tree, and with them every element left describing nothing.</summary>
    private static void Remove(PdfReader reader, HashSet<long> orphaned)
    {
        var structTreeRoot = reader.Catalog?.GetAsDict(PdfName.Structtreeroot);

        if (structTreeRoot == null)
        {
            return;
        }

        var kept = new PdfArray();

        foreach (var kid in PdfStructureTreePruner.Children(structTreeRoot.Get(PdfName.K)))
        {
            if (Keep(kid, inheritedPage: null, orphaned))
            {
                kept.Add(kid);
            }
        }

        structTreeRoot.Put(PdfName.K, kept);
    }

    /// <summary>
    ///     Decides one structure element. Everything that is not one of the retagged ids is carried
    ///     across untouched: this removes what it was asked to remove and nothing else.
    /// </summary>
    private static bool Keep(PdfObject node, int? inheritedPage, HashSet<long> orphaned)
    {
        if (PdfReader.GetPdfObject(node) is not PdfDictionary element || element.Get(PdfName.S) == null)
        {
            return true;
        }

        var page = PageOf(element, inheritedPage);
        var children = PdfStructureTreePruner.Children(element.Get(PdfName.K));
        var kept = new PdfArray();

        foreach (var child in children)
        {
            if (IsOrphaned(child, page, orphaned) || !Keep(child, page, orphaned))
            {
                continue;
            }

            kept.Add(child);
        }

        // An element that had children and has none left described only what has just become an
        // artifact, so it goes the same way. One that never had any is left as it is.
        if (kept.Size == 0 && children.Count > 0)
        {
            PdfReader.KillIndirect(node);

            return false;
        }

        element.Put(PdfName.K, kept);

        return true;
    }

    /// <summary>Whether a child is one of the marked content ids that has just been retagged.</summary>
    private static bool IsOrphaned(PdfObject child, int? page, HashSet<long> orphaned)
    {
        var resolved = PdfReader.GetPdfObject(child);

        if (resolved is PdfNumber mcid)
        {
            return page.HasValue && orphaned.Contains(Key(page.Value, mcid.IntValue));
        }

        if (resolved is not PdfDictionary dictionary || !PdfName.Mcr.Equals(dictionary.Get(PdfName.TYPE)))
        {
            return false;
        }

        var number = dictionary.GetAsNumber(PdfName.Mcid);
        var owner = PageOf(dictionary, page);

        return number != null && owner.HasValue && orphaned.Contains(Key(owner.Value, number.IntValue));
    }

    private static long Key(int page, int mcid) => ((long)page << 32) | (uint)mcid;

    private static int? PageOf(PdfDictionary element, int? inherited) =>
        element.Get(PdfName.Pg) is PrIndirectReference page ? page.Number : inherited;

    /// <summary>A marked content sequence: where its BDC is written, and what it says.</summary>
    private sealed class Sequence
    {
        public Sequence(int start, int end, int mcid, bool isArtifact)
        {
            Start = start;
            End = end;
            Mcid = mcid;
            IsArtifact = isArtifact;
        }

        public int Start { get; }

        public int End { get; }

        public int Mcid { get; }

        public bool IsArtifact { get; }
    }
}
