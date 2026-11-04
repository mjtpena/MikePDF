namespace MikePDF.Ink.Tests;

/// <summary>INK-05: the batching table, with an injectable clock (ink-pen.md 3.7).</summary>
public sealed class Ink05BatchTests
{
    private sealed class FakeClock : IInkClock
    {
        public long NowMs { get; set; }
    }

    private static readonly BatchKey Key = new(Page: 3, InkTool.Pen, ColourRgb: 0x000000u, BaseWidth: 2f, Opacity: 1f);

    [Fact]
    public void Ink05_TheFirstStrokeStartsABatch()
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        Assert.False(policy.IsOpen);
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 10));
        Assert.True(policy.IsOpen);
        Assert.Equal(1, policy.StrokeCount);
        Assert.Equal(10, policy.PointCount);
    }

    [Fact]
    public void Ink05_StrokesWithTheSameKeyWithinTenSecondsContinue()
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        policy.AddStroke(Key, 10);
        clock.NowMs += 3_000;
        Assert.Equal(BatchDecision.Continue, policy.AddStroke(Key, 20));
        Assert.Equal(2, policy.StrokeCount);
        Assert.Equal(30, policy.PointCount);
    }

    public static TheoryData<string, BatchKey> DifferentKeys => new()
    {
        { "page", Key with { Page = 4 } },
        { "tool", Key with { Tool = InkTool.Highlighter } },
        { "colour", Key with { ColourRgb = 0xFF0000u } },
        { "width", Key with { BaseWidth = 2.5f } },
        { "opacity", Key with { Opacity = 0.5f } },
    };

    [Theory]
    [MemberData(nameof(DifferentKeys))]
    public void Ink05_AChangeOfPageToolColourWidthOrOpacityStartsANewBatch(string what, BatchKey other)
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        policy.AddStroke(Key, 10);
        clock.NowMs += 100;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(other, 5));
        Assert.True(what.Length > 0);
        Assert.Equal(1, policy.StrokeCount);
        Assert.Equal(5, policy.PointCount);

        // The new batch is now the open one.
        clock.NowMs += 100;
        Assert.Equal(BatchDecision.Continue, policy.AddStroke(other, 5));
    }

    [Fact]
    public void Ink05_AGapOfTenSecondsStartsANewBatchButJustUnderContinues()
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        policy.AddStroke(Key, 1);
        clock.NowMs += 9_999;
        Assert.Equal(BatchDecision.Continue, policy.AddStroke(Key, 1));
        clock.NowMs += 10_000;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 1));
    }

    [Fact]
    public void Ink05_TheIdleGapIsMeasuredFromThePreviousStrokeNotTheFirst()
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        policy.AddStroke(Key, 1);
        for (var i = 0; i < 20; i++)
        {
            clock.NowMs += 9_000; // 180 s in total, but never 10 s between strokes
            Assert.Equal(BatchDecision.Continue, policy.AddStroke(Key, 1));
        }
    }

    [Fact]
    public void Ink05_ABatchHoldsAtMostTwoHundredStrokes()
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 1));
        for (var i = 2; i <= StrokeLimits.MaxStrokesPerBatch; i++)
        {
            clock.NowMs += 10;
            Assert.Equal(BatchDecision.Continue, policy.AddStroke(Key, 1));
        }

        Assert.Equal(200, policy.StrokeCount);
        clock.NowMs += 10;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 1));
        Assert.Equal(1, policy.StrokeCount);
    }

    [Fact]
    public void Ink05_ABatchHoldsAtMostTwentyThousandPoints()
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        policy.AddStroke(Key, 10_000);
        clock.NowMs += 10;
        Assert.Equal(BatchDecision.Continue, policy.AddStroke(Key, 10_000));
        Assert.Equal(20_000, policy.PointCount);
        clock.NowMs += 10;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 1));
        Assert.Equal(1, policy.PointCount);
    }

    [Fact]
    public void Ink05_AStrokeThatWouldOverflowThePointLimitStartsANewBatchAndAHugeStrokeStandsAlone()
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        policy.AddStroke(Key, 15_000);
        clock.NowMs += 10;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 6_000));
        clock.NowMs += 10;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 20_000));
        clock.NowMs += 10;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 1));
    }

    [Theory]
    [InlineData(BatchCloseReason.PageChange)]
    [InlineData(BatchCloseReason.StyleChange)]
    [InlineData(BatchCloseReason.SelectionChange)]
    [InlineData(BatchCloseReason.Save)]
    [InlineData(BatchCloseReason.UndoPastFirst)]
    [InlineData(BatchCloseReason.AfterUndo)]
    [InlineData(BatchCloseReason.Idle)]
    public void Ink05_EveryCloseReasonEndsTheBatchSoTheNextStrokeStartsANewOne(BatchCloseReason reason)
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock);
        policy.AddStroke(Key, 10);
        clock.NowMs += 100;
        policy.AddStroke(Key, 10);
        Assert.Null(policy.LastCloseReason);

        policy.CloseBatch(reason);
        Assert.False(policy.IsOpen);
        Assert.Equal(0, policy.StrokeCount);
        Assert.Equal(0, policy.PointCount);
        Assert.Equal(reason, policy.LastCloseReason);

        clock.NowMs += 100;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 7));
        Assert.Equal(1, policy.StrokeCount);
        Assert.Equal(7, policy.PointCount);
    }

    [Fact]
    public void Ink05_ClosingWhenNothingIsOpenIsHarmless()
    {
        var policy = new BatchPolicy(new FakeClock());
        policy.CloseBatch(BatchCloseReason.Save);
        Assert.False(policy.IsOpen);
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 1));
    }

    [Fact]
    public void Ink05_AClockThatRunsBackwardsStartsANewBatch()
    {
        var clock = new FakeClock { NowMs = 50_000 };
        var policy = new BatchPolicy(clock);
        policy.AddStroke(Key, 1);
        clock.NowMs = 40_000;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 1));
    }

    [Fact]
    public void Ink05_CustomLimitsAreHonoured()
    {
        var clock = new FakeClock();
        var policy = new BatchPolicy(clock, maxStrokes: 2, maxPoints: 50, idleMs: 500);
        policy.AddStroke(Key, 10);
        clock.NowMs += 499;
        Assert.Equal(BatchDecision.Continue, policy.AddStroke(Key, 10));
        clock.NowMs += 10;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 10)); // third stroke
        clock.NowMs += 500;
        Assert.Equal(BatchDecision.StartNew, policy.AddStroke(Key, 10)); // idle
    }

    [Fact]
    public void Ink05_ConstructorAndArgumentsAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => new BatchPolicy(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchPolicy(new FakeClock(), maxStrokes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchPolicy(new FakeClock(), maxPoints: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchPolicy(new FakeClock(), idleMs: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchPolicy(new FakeClock()).AddStroke(Key, -1));
    }
}
