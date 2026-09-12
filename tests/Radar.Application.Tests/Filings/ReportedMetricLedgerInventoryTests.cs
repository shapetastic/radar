using Radar.Application.Filings;
using Radar.Application.NewsRisk.Judgment;
using Radar.Application.Pipeline;

namespace Radar.Application.Tests.Filings;

/// <summary>
/// Spec 223 — the ONE not-recorded rendering of <c>LedgerEntriesOnDisk</c> lives on
/// <see cref="ReportedMetricLedgerInventory"/> and BOTH renderers (the collection pass's ledger line and
/// the judge's per-cohort line) route through it, so the "ledger not registered" / "inventory
/// unavailable" / "not recorded (&lt;reason&gt;)" / "reason not stated" texts cannot drift between the two.
/// The three branches are asserted once here; the two renderers are pinned to the SAME text for the same
/// input, and neither ever renders a defaulted <c>0</c>.
/// </summary>
public sealed class ReportedMetricLedgerInventoryTests
{
    // ---- the three not-recorded branches, asserted once --------------------------------------------------

    [Fact]
    public void DescribeNotRecorded_WhenTheLedgerIsNotRegistered_SaysSo_WhateverInventoryWasHandedIn()
    {
        // Registration wins over any inventory: an unregistered ledger has nothing to inventory, so even a
        // measured value handed in (a caller bug) renders as not-registered rather than as a number.
        const string expected = "not recorded (ledger not registered)";
        Assert.Equal(expected, ReportedMetricLedgerInventory.DescribeNotRecorded(null, ledgerRegistered: false));
        Assert.Equal(
            expected,
            ReportedMetricLedgerInventory.DescribeNotRecorded(ReportedMetricLedgerInventory.AbsentRoot, ledgerRegistered: false));
        Assert.Equal(
            expected,
            ReportedMetricLedgerInventory.DescribeNotRecorded(new ReportedMetricLedgerInventory(true, 3, 7, 0, null), ledgerRegistered: false));
    }

    [Fact]
    public void DescribeNotRecorded_WhenNoInventoryWasKept_SaysUnavailable()
    {
        Assert.Equal(
            "not recorded (inventory unavailable)",
            ReportedMetricLedgerInventory.DescribeNotRecorded(null, ledgerRegistered: true));
    }

    [Fact]
    public void DescribeNotRecorded_WhenTheStoreCouldNotEstablishTheCounts_NamesTheReason()
    {
        Assert.Equal(
            "not recorded (ledger root could not be enumerated: IOException)",
            ReportedMetricLedgerInventory.DescribeNotRecorded(
                ReportedMetricLedgerInventory.NotRecorded("ledger root could not be enumerated: IOException"),
                ledgerRegistered: true));
    }

    [Fact]
    public void DescribeNotRecorded_WhenTheCountsAreMissingAndNoReasonWasGiven_SaysReasonNotStated()
    {
        // The public constructor permits a null-count inventory with no reason; the fallback names the
        // gap instead of rendering nothing (or a zero).
        Assert.Equal(
            "not recorded (reason not stated)",
            ReportedMetricLedgerInventory.DescribeNotRecorded(
                new ReportedMetricLedgerInventory(null, null, null, null, null), ledgerRegistered: true));
    }

    [Fact]
    public void DescribeNotRecorded_WhenOnlyOneCountIsRecorded_TreatsTheInventoryAsNotRecorded()
    {
        // One measured number beside one defaulted number would be the defaulted-zero-as-measured-zero
        // defect; a partially recorded inventory is therefore not recorded at all.
        var recordsOnly = new ReportedMetricLedgerInventory(true, LedgerFiles: null, LedgerRecords: 4, UnreadableFiles: 0, NotRecordedReason: null);
        var filesOnly = new ReportedMetricLedgerInventory(true, LedgerFiles: 2, LedgerRecords: null, UnreadableFiles: 0, NotRecordedReason: null);

        Assert.False(recordsOnly.IsRecorded);
        Assert.False(filesOnly.IsRecorded);
        Assert.Equal("not recorded (reason not stated)", ReportedMetricLedgerInventory.DescribeNotRecorded(recordsOnly, ledgerRegistered: true));
        Assert.Equal("not recorded (reason not stated)", ReportedMetricLedgerInventory.DescribeNotRecorded(filesOnly, ledgerRegistered: true));
        Assert.Throws<InvalidOperationException>(() => recordsOnly.RecordedCounts);
    }

    [Fact]
    public void DescribeNotRecorded_WhenTheInventoryIsMeasured_ReturnsNull_AndTheCountsAreReadable()
    {
        // The measured zero of an absent root IS recorded — the caller renders it, never the helper.
        Assert.Null(ReportedMetricLedgerInventory.DescribeNotRecorded(ReportedMetricLedgerInventory.AbsentRoot, ledgerRegistered: true));
        Assert.True(ReportedMetricLedgerInventory.AbsentRoot.IsRecorded);
        Assert.Equal((0, 0), ReportedMetricLedgerInventory.AbsentRoot.RecordedCounts);

        var populated = new ReportedMetricLedgerInventory(true, LedgerFiles: 3, LedgerRecords: 7, UnreadableFiles: 1, NotRecordedReason: null);
        Assert.Null(ReportedMetricLedgerInventory.DescribeNotRecorded(populated, ledgerRegistered: true));
        Assert.Equal((7, 3), populated.RecordedCounts);
    }

    [Fact]
    public void RecordedCounts_OnANotRecordedInventory_Throws_RatherThanDefaultingToZero()
    {
        Assert.Throws<InvalidOperationException>(() => ReportedMetricLedgerInventory.NotRecorded("x").RecordedCounts);
    }

    // ---- both renderers route through the helper -------------------------------------------------------

    public static TheoryData<ReportedMetricLedgerInventory?, bool> NotRecordedInputs => new()
    {
        { null, false },
        { ReportedMetricLedgerInventory.AbsentRoot, false },
        { null, true },
        { ReportedMetricLedgerInventory.NotRecorded("inventory read failed: IOException"), true },
        { new ReportedMetricLedgerInventory(null, null, null, null, null), true },
    };

    [Theory]
    [MemberData(nameof(NotRecordedInputs))]
    public void BothRenderers_RenderTheSharedNotRecordedText_AndNeverAZero(
        ReportedMetricLedgerInventory? inventory, bool ledgerRegistered)
    {
        var expected = ReportedMetricLedgerInventory.DescribeNotRecorded(inventory, ledgerRegistered);
        Assert.NotNull(expected);

        Assert.Equal(expected, CollectionPass.RenderLedgerInventory(inventory, ledgerRegistered));
        Assert.Equal(expected, NewsJudgmentGenerator.RenderLedgerEntriesOnDisk(inventory, ledgerRegistered));
        Assert.StartsWith("not recorded (", expected, StringComparison.Ordinal);
        Assert.DoesNotContain("0", expected, StringComparison.Ordinal);
    }

    [Fact]
    public void BothRenderers_RenderTheMeasuredAbsentRootZero_InTheirOwnForm()
    {
        // The measured form is each line's own: the pass line is the richer one (files, unreadable, root
        // state); the judge line is the record count with the root state beside a zero.
        Assert.Equal(
            "0 record(s) in 0 ledger file(s) (0 unreadable; ledger root directory absent)",
            CollectionPass.RenderLedgerInventory(ReportedMetricLedgerInventory.AbsentRoot, ledgerRegistered: true));
        Assert.Equal(
            "0 (ledger root directory absent)",
            NewsJudgmentGenerator.RenderLedgerEntriesOnDisk(ReportedMetricLedgerInventory.AbsentRoot, ledgerRegistered: true));
    }
}
