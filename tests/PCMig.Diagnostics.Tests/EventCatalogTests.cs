using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D1 契约：EventCatalog 的结构不变量。
/// 这些用例锁的是"EventId 是永久契约"这件事：编号唯一、段位正确、永不与废弃编号相交、Secret 不得登记。
/// </summary>
public sealed class EventCatalogTests
{
    [Fact]
    public void All_EventIdsAreUnique()
    {
        var ids = EventCatalog.All.Select(d => d.EventId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void All_CodesAndNamesAndBasesFollowCategoryMath()
    {
        var offenders = new List<string>();
        foreach (var d in EventCatalog.All)
        {
            var prefix = d.Category.Prefix();
            var expectedCode = prefix + "-" + d.Ordinal.ToString("000", CultureInfo.InvariantCulture);
            var expectedId = d.Category.EventIdBase() + d.Ordinal;

            if (!string.Equals(d.Code, expectedCode, StringComparison.Ordinal))
                offenders.Add($"{d.Name}: Code={d.Code} 期望 {expectedCode}");
            if (d.EventId != expectedId)
                offenders.Add($"{d.Name}: EventId={d.EventId} 期望 {expectedId}");
            if (!d.Name.StartsWith(prefix + ".", StringComparison.Ordinal))
                offenders.Add($"{d.Name}: 名称前缀与族不一致（应为 {prefix}.）");
            if (!d.IsKnown)
                offenders.Add($"{d.Name}: 已登记事件不得是 Unknown 族");
            if (d.Version < 1)
                offenders.Add($"{d.Name}: Version 必须 >= 1");
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void RetiredEventIds_AreNeverReused()
    {
        var reused = EventCatalog.All.Select(d => d.EventId).Intersect(EventCatalog.RetiredEventIds).ToArray();
        Assert.Empty(reused);
    }

    /// <summary>Secret 永远不得进入 DiagnosticEvent：catalog 侧就不允许存在这种事件。</summary>
    [Fact]
    public void NoCatalogEntryIsClassifiedSecret()
    {
        var secret = EventCatalog.All
            .Where(d => d.Privacy == PrivacyClassification.Secret)
            .Select(d => d.Name)
            .ToArray();
        Assert.Empty(secret);
    }

    [Fact]
    public void Define_RejectsSecretClassification()
    {
        var ex = Assert.Throws<ArgumentException>(() => EventDescriptor.Define(
            DiagnosticCategory.Ui, 900, "UI.SecretThing",
            DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Secret));

        Assert.Contains("Secret", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-1)]
    public void Define_RejectsOrdinalOutOfRange(int ordinal)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EventDescriptor.Define(
            DiagnosticCategory.Ui, ordinal, "UI.Anything",
            DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public));
    }

    [Fact]
    public void Define_RejectsUnknownCategoryAndMismatchedNamePrefix()
    {
        Assert.Throws<ArgumentException>(() => EventDescriptor.Define(
            DiagnosticCategory.Unknown, 1, "UNK.Thing",
            DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public));

        Assert.Throws<ArgumentException>(() => EventDescriptor.Define(
            DiagnosticCategory.Ui, 21, "NET.WrongFamily",
            DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Public));
    }

    [Fact]
    public void LookupsAreConsistentAcrossCodeIdAndName()
    {
        foreach (var d in EventCatalog.All)
        {
            Assert.True(EventCatalog.TryGetByCode(d.Code, out var byCode));
            Assert.True(EventCatalog.TryGetByEventId(d.EventId, out var byId));
            Assert.True(EventCatalog.TryGetByName(d.Name, out var byName));
            Assert.Same(d, byCode);
            Assert.Same(d, byId);
            Assert.Same(d, byName);
        }
    }

    [Fact]
    public void UnknownLookupsReturnFalseWithoutThrowing()
    {
        Assert.False(EventCatalog.TryGetByCode("ZZ-999", out _));
        Assert.False(EventCatalog.TryGetByEventId(-424242, out _));
        Assert.False(EventCatalog.TryGetByName("ZZ.Nope", out _));
        Assert.Throws<KeyNotFoundException>(() => EventCatalog.GetByCode("ZZ-999"));
    }

    [Fact]
    public void CatalogHashIsStableLowercaseHex()
    {
        Assert.Equal(64, EventCatalog.CatalogHash.Length);
        Assert.Equal(EventCatalog.CatalogHash.ToLowerInvariant(), EventCatalog.CatalogHash);
        Assert.Contains(EventCatalog.All, d => d == UiEvents.UserActionObserved);
    }

    [Fact]
    public void SpecificPinnedIdsMatchThePublishedContract()
    {
        // 这些编号一旦发布就不得漂移（架构方案 §7 的段位表）。
        Assert.Equal(2001, UiEvents.UserActionObserved.EventId);
        Assert.Equal("UI-001", UiEvents.UserActionObserved.Code);
        Assert.Equal(3001, NetEvents.ProbeStarted.EventId);
        Assert.Equal(4001, FsEvents.ScanStarted.EventId);
        Assert.Equal(5001, PlanEvents.PlanRequested.EventId);
        Assert.Equal(6001, PreflightEvents.PreflightStarted.EventId);
        Assert.Equal(7001, TransferEvents.JobRunStarted.EventId);
        Assert.Equal(8001, RobocopyEvents.ProcessStartRequested.EventId);
        Assert.Equal(9001, PersistenceEvents.WriteStarted.EventId);
        Assert.Equal(10001, VerifyEvents.VerifyStarted.EventId);
        Assert.Equal(11001, RepairEvents.RepairRequested.EventId);
        Assert.Equal(12001, DiagnosticsEvents.SessionStarted.EventId);
        Assert.Equal(1001, AppEvents.ProcessStarted.EventId);
    }

    [Fact]
    public void UnknownDescriptorPreservesRawIdentity()
    {
        var unknown = EventDescriptor.Unknown(777777, "ZZ-777", "ZZ.SomethingNew");

        Assert.False(unknown.IsKnown);
        Assert.Equal(777777, unknown.EventId);
        Assert.Equal("ZZ-777", unknown.Code);
        Assert.Equal("ZZ.SomethingNew", unknown.Name);

        var noName = EventDescriptor.Unknown(4242, null, null);
        Assert.Equal(EventDescriptor.UnknownCode, noName.Code);
        Assert.False(noName.IsKnown);
    }

    /// <summary>返工护栏：Robocopy 的文件行事件必须叫"尝试"，不得被写成"复制成功"。</summary>
    [Fact]
    public void RobocopyFileEventIsAnAttemptNotASuccessClaim()
    {
        Assert.Equal("RBC.FileAttemptObserved", RobocopyEvents.FileAttemptObserved.Name);
        Assert.DoesNotContain(EventCatalog.All, d => d.Name.Contains("FileCopySucceeded", StringComparison.Ordinal));
    }
}