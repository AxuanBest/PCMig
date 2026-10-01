using System;
using System.Linq;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D1 契约：NoOp 发布器（诊断未装配时的默认实现）必须"绝不干扰业务"。
/// 它是整个系统 fail-open 的地基：OFF 路径必须零判断成本、零异常、零 payload 构造。
/// </summary>
public sealed class NoOpPublisherTests
{
    [Fact]
    public void NoOpReportsDisabledAndNeverAccepts()
    {
        var publisher = NoOpDiagnosticPublisher.Instance;

        Assert.False(publisher.IsEnabled);
        Assert.Equal(CaptureMode.Off, publisher.Mode);

        var context = DiagnosticContext.Root(TestEvents.FixedSessionId, "ConnectionViewModel");
        Assert.False(publisher.TryPublish(new DiagnosticEventDraft(UiEvents.UserActionObserved, context)));
        Assert.False(publisher.TryPublish(new DiagnosticEventDraft(
            NetEvents.SmbConnectFailed, context,
            HResult: unchecked((int)0x80070035), ErrorDomain: ErrorDomain.Win32, Win32Error: 53,
            Outcome: DiagnosticOutcome.Failed)));
    }

    [Fact]
    public void NoOpIsDisabledForEveryCatalogEvent()
    {
        var publisher = NoOpDiagnosticPublisher.Instance;
        var enabled = EventCatalog.All.Where(d => publisher.IsEnabledFor(d)).Select(d => d.Code).ToArray();
        Assert.Empty(enabled);
    }

    [Fact]
    public void NoOpIsASingleton()
    {
        Assert.Same(NoOpDiagnosticPublisher.Instance, NoOpDiagnosticPublisher.Instance);
    }

    [Fact]
    public void DiagnosticContextScopesAreImmutable()
    {
        var root = DiagnosticContext.Root(TestEvents.FixedSessionId, "Shell");
        var withJob = root.WithJob("JOB-1").WithObject("obj-1").WithControl("Step1.Connect");

        // 原上下文不得被改写（共享实例被就地改写会让晚到事件挂错作用域）。
        Assert.Null(root.JobId);
        Assert.Null(root.ObjectId);
        Assert.Null(root.ControlId);

        Assert.Equal("JOB-1", withJob.JobId);
        Assert.Equal("obj-1", withJob.ObjectId);
        Assert.Equal("Step1.Connect", withJob.ControlId);
        Assert.Equal(TestEvents.FixedSessionId, withJob.SessionId);
    }

    [Fact]
    public void WithOperationResetsAttemptAndKeepsRunGenerationWhenNotGiven()
    {
        var ctx = DiagnosticContext.Root(TestEvents.FixedSessionId)
            .WithAttempt(4)
            .WithOperation(Guid.NewGuid(), runGeneration: 7);

        Assert.Equal(7, ctx.RunGeneration);
        Assert.Equal(1, ctx.Attempt);

        var sameGen = ctx.WithOperation(Guid.NewGuid());
        Assert.Equal(7, sameGen.RunGeneration);

        // 非法尝试次数回落为 1，而不是留下 0/负数这种不可能值。
        Assert.Equal(1, ctx.WithAttempt(0).Attempt);
        Assert.Equal(1, ctx.WithAttempt(-5).Attempt);
    }

    [Fact]
    public void EventRefFormatsForDisplayButKeepsFullIdentity()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var reference = new EventRef(id, 42);

        Assert.False(reference.IsNone);
        Assert.Equal("aaaaaaaa#42", reference.ToString());
        Assert.Equal(id, reference.SessionId);
        Assert.Equal(42, reference.Sequence);

        Assert.NotEqual(reference, new EventRef(id, 43));
        Assert.NotEqual(reference, new EventRef(Guid.NewGuid(), 42));
    }

    [Fact]
    public void PathRefUnavailableVariantsNeverCarryPlaintext()
    {
        var unavailable = PathRef.Unavailable(PathRole.Source);
        Assert.Equal("[unavailable]", unavailable.PathToken);

        var tokenMissing = PathRef.TokenUnavailable(PathRole.Target, "Unc", "dst");
        Assert.Equal("[token-unavailable]", tokenMissing.PathToken);

        // 占位符里不得出现盘符/UNC 形态（防止有人"顺手"塞明文路径）。
        foreach (var token in new[] { unavailable.PathToken, tokenMissing.PathToken })
        {
            Assert.DoesNotContain(":\\", token, StringComparison.Ordinal);
            Assert.DoesNotContain("\\\\", token, StringComparison.Ordinal);
        }
    }
}