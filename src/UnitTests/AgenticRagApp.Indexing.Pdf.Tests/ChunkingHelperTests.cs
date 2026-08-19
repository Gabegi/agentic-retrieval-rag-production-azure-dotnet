using AgenticRagApp.Indexing.Pdf.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class ChunkingHelperTests
{
    [TestMethod]
    public void EstimateTokens_EmptyContent_IsZero()
    {
        Assert.AreEqual(0, ChunkingHelper.EstimateTokens("", isTable: false));
        Assert.AreEqual(0, ChunkingHelper.EstimateTokens("", isTable: true));
    }

    [TestMethod]
    public void EstimateTokens_Prose_UsesProseRatioAndRoundsUp()
    {
        // 10 chars / 3.1 = 3.226... -> ceil to 4.
        var tokens = ChunkingHelper.EstimateTokens(new string('a', 10), isTable: false);

        Assert.AreEqual(4, tokens);
    }

    [TestMethod]
    public void EstimateTokens_Table_UsesTableRatioAndRoundsUp()
    {
        // 10 chars / 1.8 = 5.55... -> ceil to 6. The table ratio was 2.2 until it was
        // re-measured with the real tokenizer over the whole cached text of the big four:
        // two documents came back below 2.20 (CAO VVT 1.88, CAO GHZ 2.00), which made the
        // old constant underestimate table tokens by ~17% - the direction that silently
        // overruns a ceiling.
        var tokens = ChunkingHelper.EstimateTokens(new string('a', 10), isTable: true);

        Assert.AreEqual(6, tokens);
    }

    [TestMethod]
    public void EstimateTokens_SameContent_TableEstimateIsHigherThanProse()
    {
        // Table markdown tokenizes less efficiently (fewer chars/token), so the same content
        // must never estimate *fewer* tokens under the table ratio than the prose ratio.
        var content = new string('a', 500);

        var proseTokens = ChunkingHelper.EstimateTokens(content, isTable: false);
        var tableTokens = ChunkingHelper.EstimateTokens(content, isTable: true);

        Assert.IsTrue(tableTokens > proseTokens);
    }

    [TestMethod]
    public void SafeKey_IsUrlSafeBase64_NoPlusOrSlash()
    {
        // Pick inputs whose base64 encoding is known to contain '+' and '/' before replacement.
        var key = ChunkingHelper.SafeKey("blob>>??", 999999);

        Assert.IsFalse(key.Contains('+'));
        Assert.IsFalse(key.Contains('/'));
    }

    [TestMethod]
    public void SafeKey_SameInputs_AreDeterministic()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 3);
        var key2 = ChunkingHelper.SafeKey("doc1", 3);

        Assert.AreEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_DifferentIndex_ProducesDifferentKey()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 0);
        var key2 = ChunkingHelper.SafeKey("doc1", 1);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_DifferentBlobName_ProducesDifferentKey()
    {
        var key1 = ChunkingHelper.SafeKey("doc1", 0);
        var key2 = ChunkingHelper.SafeKey("doc2", 0);

        Assert.AreNotEqual(key1, key2);
    }

    [TestMethod]
    public void SafeKey_Decodes_BackToBlobNameAndIndex()
    {
        var key = ChunkingHelper.SafeKey("some::blob/name", 42);

        var restored = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/')));

        Assert.AreEqual("some::blob/name::42", restored);
    }

    // ── CharBudgetForTokens ──────────────────────────────────────────────────

    [TestMethod]
    public void CharBudgetForTokens_Prose_IsTheTokenCeilingTimesTheProseRatio()
    {
        // 512 * 3.1 = 1,587.2, truncated to 1,587.
        Assert.AreEqual(1_587, ChunkingHelper.CharBudgetForTokens(512, isTable: false));
    }

    [TestMethod]
    public void CharBudgetForTokens_Table_IsSmallerThanTheProseBudgetForTheSameCeiling()
    {
        // Table markdown costs more tokens per character, so the same token ceiling has to buy
        // fewer characters. A budget that got this backwards would size table cuts as if they
        // were prose and overrun the ceiling by the full width of the ratio gap.
        Assert.IsTrue(
            ChunkingHelper.CharBudgetForTokens(512, isTable: true) <
            ChunkingHelper.CharBudgetForTokens(512, isTable: false));
    }

    [TestMethod]
    public void CharBudgetForTokens_RoundTripsUnderTheCeilingItWasSizedFor()
    {
        // The two directions have to agree: text filling the returned budget must not estimate
        // above the ceiling that produced it. HardCutter sizes its window this way and then
        // counts the pieces exactly, so a budget that overshot would produce cuts that fail
        // the later exact check.
        foreach (var isTable in new[] { false, true })
        {
            var budget = ChunkingHelper.CharBudgetForTokens(512, isTable);

            Assert.IsTrue(
                ChunkingHelper.EstimateTokens(new string('a', budget), isTable) <= 512,
                $"Budget of {budget} chars estimated above the 512-token ceiling (isTable: {isTable}).");
        }
    }

    [TestMethod]
    public void CharBudgetForTokens_ZeroCeiling_IsZero()
    {
        // HardCutter clamps this to a minimum of 1 itself; the helper is not the place that
        // invents a non-zero window.
        Assert.AreEqual(0, ChunkingHelper.CharBudgetForTokens(0, isTable: false));
    }

    // ── TitleLine ────────────────────────────────────────────────────────────

    [TestMethod]
    public void TitleLine_NoDomainTag_IsJustTheTitle()
    {
        Assert.AreEqual("Gedragscode medewerkers", ChunkingHelper.TitleLine("Gedragscode medewerkers", null));
        Assert.AreEqual("Gedragscode medewerkers", ChunkingHelper.TitleLine("Gedragscode medewerkers", ""));
    }

    [TestMethod]
    public void TitleLine_WithDomainTag_AppendsItInBrackets()
    {
        Assert.AreEqual("Zorgplan [vvt]", ChunkingHelper.TitleLine("Zorgplan", "vvt"));
    }

    [TestMethod]
    public void TitleLine_NullTitle_IsEmptyRatherThanTheWordNull()
    {
        // This line is prepended to the embedded text, so a "null" here would be embedded
        // verbatim on every chunk of an untitled document.
        Assert.AreEqual("", ChunkingHelper.TitleLine(null, null));
    }

    [TestMethod]
    public void TitleLine_NullTitleWithDomainTag_StillCarriesTheTag()
    {
        // The sector tag is what keeps a VVT answer out of a GHZ question; losing it because
        // the title happens to be missing is the failure worth pinning.
        Assert.AreEqual(" [ghz]", ChunkingHelper.TitleLine(null, "ghz"));
    }

    [TestMethod]
    public void TitleLine_IsTheSameLineTheBudgetAndTheEmbeddedTextBothUse()
    {
        // One rule, one call site each side - PrefixBuilder builds the real prefix from this
        // and the cascade charges it against the ceiling, so any divergence here silently
        // means the budgeted prefix is not the embedded one.
        var title  = "Arbeidsvoorwaarden";
        var tagged = ChunkingHelper.TitleLine(title, "vvt");

        Assert.IsTrue(tagged.StartsWith(title, StringComparison.Ordinal));
        Assert.IsTrue(tagged.Length > title.Length);
    }

    // ── ContainsTable ────────────────────────────────────────────────────────

    [TestMethod]
    public void ContainsTable_TwoConsecutiveRows_IsATable()
    {
        var content = "| Functie | Schaal |\n|---|---|";

        Assert.IsTrue(ChunkingHelper.ContainsTable(content));
    }

    [TestMethod]
    public void ContainsTable_OneLineWithPipes_IsProseNotATable()
    {
        // A single line containing '|' is prose with a pipe in it. This predicate is what the
        // index's has_table is built from, so a lone pipe marking a chunk as tabular would
        // mislabel ordinary text corpus-wide.
        Assert.IsFalse(ChunkingHelper.ContainsTable("kies optie A | optie B en ga verder"));
    }

    [TestMethod]
    public void ContainsTable_TwoRowsSeparatedByProse_IsNotATable()
    {
        // The two rows have to be CONSECUTIVE - the run counter resets on any non-row line.
        var content = "| a | b |\ngewone zin ertussen\n| c | d |";

        Assert.IsFalse(ChunkingHelper.ContainsTable(content));
    }

    [TestMethod]
    public void ContainsTable_TableAfterLeadingProse_IsStillFound()
    {
        var content = "Zie onderstaande tabel:\n\n| Functie | Schaal |\n| FWG 35 | 10 |\n\nEinde.";

        Assert.IsTrue(ChunkingHelper.ContainsTable(content));
    }

    [TestMethod]
    public void ContainsTable_EmptyOrWhitespace_IsFalse()
    {
        Assert.IsFalse(ChunkingHelper.ContainsTable(""));
        Assert.IsFalse(ChunkingHelper.ContainsTable("   \n  "));
    }

    [TestMethod]
    public void ContainsTable_AgreesWithSplitIntoBlocks()
    {
        // The two share the 2-consecutive-rows rule deliberately, kept beside one another so
        // there is one definition of "markdown table row" rather than two that drift.
        var content = "intro\n| a | b |\n|---|---|\nuitro";

        Assert.AreEqual(
            ChunkingHelper.SplitIntoBlocks(content).Any(b => b.IsTable),
            ChunkingHelper.ContainsTable(content));
    }

    // ── SplitIntoBlocks ──────────────────────────────────────────────────────

    [TestMethod]
    public void SplitIntoBlocks_ProseOnly_IsASingleProseBlock()
    {
        var blocks = ChunkingHelper.SplitIntoBlocks("regel een\nregel twee");

        Assert.AreEqual(1, blocks.Count);
        Assert.IsFalse(blocks[0].IsTable);
        Assert.AreEqual("regel een\nregel twee", blocks[0].Text);
    }

    [TestMethod]
    public void SplitIntoBlocks_ProseThenTableThenProse_AlternatesThreeBlocks()
    {
        var blocks = ChunkingHelper.SplitIntoBlocks("intro\n| a | b |\n| c | d |\nuitro");

        CollectionAssert.AreEqual(new[] { false, true, false }, blocks.Select(b => b.IsTable).ToList());
        Assert.AreEqual("intro", blocks[0].Text);
        Assert.AreEqual("| a | b |\n| c | d |", blocks[1].Text);
        Assert.AreEqual("uitro", blocks[2].Text);
    }

    [TestMethod]
    public void SplitIntoBlocks_LoneTableLine_IsDemotedToProseAndMergedWithItsNeighbours()
    {
        // Demoting a one-line "table" would leave two prose runs adjacent; leaving them split
        // would hand the caller two blocks where the text has one paragraph, and each would be
        // sized and cut on its own.
        var blocks = ChunkingHelper.SplitIntoBlocks("intro\n| eenzaam |\nuitro");

        Assert.AreEqual(1, blocks.Count);
        Assert.IsFalse(blocks[0].IsTable);
        Assert.AreEqual("intro\n| eenzaam |\nuitro", blocks[0].Text);
    }

    [TestMethod]
    public void SplitIntoBlocks_TableAtTheStart_KeepsItsRowsTogether()
    {
        var blocks = ChunkingHelper.SplitIntoBlocks("| a | b |\n|---|---|\n| c | d |\nnawoord");

        Assert.AreEqual(2, blocks.Count);
        Assert.IsTrue(blocks[0].IsTable);
        Assert.AreEqual(3, blocks[0].Text.Split('\n').Length);
        Assert.IsFalse(blocks[1].IsTable);
    }

    [TestMethod]
    public void SplitIntoBlocks_TwoTablesSeparatedByProse_StayTwoTables()
    {
        var blocks = ChunkingHelper.SplitIntoBlocks("| a | b |\n| c | d |\ntussen\n| e | f |\n| g | h |");

        CollectionAssert.AreEqual(new[] { true, false, true }, blocks.Select(b => b.IsTable).ToList());
    }

    [TestMethod]
    public void SplitIntoBlocks_PreservesEveryLineOfTheInput()
    {
        // The blocks are what the routing profile and the block cascade both measure, so a
        // line lost in the split is content that is never chunked and never indexed.
        var content = "intro\n| a | b |\n|---|---|\nmidden\n| eenzaam |\nslot";

        var rejoined = string.Join("\n", ChunkingHelper.SplitIntoBlocks(content).Select(b => b.Text));

        Assert.AreEqual(content, rejoined);
    }
}
