namespace iTextSharp.text.pdf;

/// <summary>
///     Where each marked content sequence on a page begins, in default user space.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="PdfStructureStamp" /> needs this to decide where a newly stamped element
///         belongs in the structure tree. The tree gives the reading order but says nothing about
///         position, and the caller stamping the content knows where it is putting it - so the two
///         only meet if the elements already in the tree can be located on the page as well.
///     </para>
///     <para>
///         Only the first drawing operation inside a sequence is recorded, and only its y. That is
///         all a top to bottom ordering needs, and it keeps the scan to one pass with no glyph
///         metrics. Sequences that draw nothing get no entry.
///     </para>
///     <para>
///         Text inside a form XObject is not reached: this reads the page content stream, not what
///         <c>Do</c> refers to. A structure element whose content lives entirely in an XObject
///         therefore has no position, which the caller has to allow for.
///     </para>
/// </remarks>
internal static class PdfContentPositions
{
    private static readonly float[] Identity = { 1, 0, 0, 1, 0, 0 };

    /// <summary>
    ///     Maps marked content id to the y of the first thing drawn inside it. Ids that draw nothing
    ///     are absent, as is every id when the content cannot be parsed.
    /// </summary>
    public static Dictionary<int, float> FirstY(byte[] content)
    {
        var found = new Dictionary<int, float>();

        if (content == null || content.Length == 0)
        {
            return found;
        }

        var parser = new PdfContentParser(new PrTokeniser(new RandomAccessFileOrArray(content)));
        var operands = new List<PdfObject>();
        var marked = new Stack<int>();
        var saved = new Stack<float[]>();
        var ctm = Identity;
        var textMatrix = Identity;
        var lineMatrix = Identity;
        var leading = 0f;

        try
        {
            while (parser.Parse(operands).Count > 0)
            {
                var op = operands[operands.Count - 1].ToString();

                switch (op)
                {
                    case "q":
                        saved.Push(ctm);

                        break;
                    case "Q":
                        if (saved.Count > 0)
                        {
                            ctm = saved.Pop();
                        }

                        break;
                    case "cm":
                        ctm = Multiply(Matrix(operands, count: 6), ctm);

                        break;
                    case "BT":
                    case "ET":
                        // Reset on the way out as well, or a matrix left over from the last line
                        // places an image drawn afterwards.
                        textMatrix = lineMatrix = Identity;

                        break;
                    case "Tm":
                        textMatrix = lineMatrix = Matrix(operands, count: 6);

                        break;
                    case "TL":
                        leading = Number(operands, back: 1);

                        break;
                    case "Td":
                        lineMatrix = textMatrix =
                            Multiply(Translation(Number(operands, back: 2), Number(operands, back: 1)), lineMatrix);

                        break;
                    case "TD":
                        leading = -Number(operands, back: 1);
                        lineMatrix = textMatrix =
                            Multiply(Translation(Number(operands, back: 2), Number(operands, back: 1)), lineMatrix);

                        break;
                    case "T*":
                        lineMatrix = textMatrix = Multiply(Translation(tx: 0, -leading), lineMatrix);

                        break;
                    case "'":
                    case "\"":
                        lineMatrix = textMatrix = Multiply(Translation(tx: 0, -leading), lineMatrix);
                        Record(found, marked, Multiply(textMatrix, ctm)[5]);

                        break;
                    case "Tj":
                    case "TJ":
                        Record(found, marked, Multiply(textMatrix, ctm)[5]);

                        break;
                    case "Do":
                    case "sh":
                    case "EI":
                        // Not text: an XObject, a shading or an inline image is placed by the
                        // current transformation alone, and the text matrix says nothing about it.
                        Record(found, marked, ctm[5]);

                        break;
                    case "BMC":
                    case "BDC":
                        marked.Push(MarkedContentId(operands));

                        break;
                    case "EMC":
                        if (marked.Count > 0)
                        {
                            marked.Pop();
                        }

                        break;
                }
            }
        }
        catch (Exception)
        {
            // A stream this cannot read leaves the elements unplaced, which is the same position
            // the caller was in before asking. Failing the stamping over it would be worse.
        }

        return found;
    }

    /// <summary>
    ///     Notes the position against the innermost sequence that has an id, and only the first time:
    ///     a sequence begins where its first content is, not where its last is.
    /// </summary>
    private static void Record(Dictionary<int, float> found, Stack<int> marked, float y)
    {
        foreach (var mcid in marked)
        {
            if (mcid < 0)
            {
                continue;
            }

            if (!found.ContainsKey(mcid))
            {
                found[mcid] = y;
            }

            return;
        }
    }

    /// <summary>The /MCID of a BDC, or -1 when the sequence carries no id or an indirect property list.</summary>
    private static int MarkedContentId(List<PdfObject> operands)
    {
        if (operands.Count < 3 || operands[operands.Count - 2] is not PdfDictionary properties)
        {
            return -1;
        }

        return properties.Get(PdfName.Mcid) is PdfNumber mcid ? mcid.IntValue : -1;
    }

    private static float[] Matrix(List<PdfObject> operands, int count)
    {
        var matrix = new float[count];

        for (var index = 0; index < count; index++)
        {
            matrix[index] = Number(operands, count - index);
        }

        return matrix;
    }

    /// <summary>Operand counted back from the operator, which is the last element.</summary>
    private static float Number(List<PdfObject> operands, int back)
    {
        var index = operands.Count - 1 - back;

        return index >= 0 && operands[index] is PdfNumber number ? (float)number.DoubleValue : 0f;
    }

    private static float[] Translation(float tx, float ty) => new[] { 1, 0, 0, 1, tx, ty };

    private static float[] Multiply(float[] m, float[] n) =>
        new[]
        {
            m[0] * n[0] + m[1] * n[2],
            m[0] * n[1] + m[1] * n[3],
            m[2] * n[0] + m[3] * n[2],
            m[2] * n[1] + m[3] * n[3],
            m[4] * n[0] + m[5] * n[2] + n[4],
            m[4] * n[1] + m[5] * n[3] + n[5]
        };
}
