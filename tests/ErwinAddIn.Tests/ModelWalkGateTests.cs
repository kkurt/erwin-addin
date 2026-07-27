using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace ErwinAddIn.Tests
{
    /// <summary>
    /// <see cref="ModelWalkGate"/> is what stops the add-in's seven UI-thread timer ticks
    /// from re-entering SCAPI while a whole-model walk holds the thread. Its whole job is
    /// the ref count: lower it one Enter too early and the ticks resume mid-walk, which is
    /// the defect that cost 46 minutes on a real model (measured 2026-07-27).
    ///
    /// <para>These tests run in one xUnit collection because the gate is static process
    /// state. They restore it by disposing every scope they open.</para>
    /// </summary>
    [Collection("ModelWalkGate")]
    public class ModelWalkGateTests
    {
        [Fact]
        public void Gate_IsDown_WhenNoWalkIsRunning()
        {
            ModelWalkGate.IsActive.Should().BeFalse(
                "no scope is open - a leaked scope from another test would silence every timer");
        }

        [Fact]
        public void Enter_RaisesTheGate_AndDisposeLowersIt()
        {
            using (ModelWalkGate.Enter("unit test"))
            {
                ModelWalkGate.IsActive.Should().BeTrue();
            }

            ModelWalkGate.IsActive.Should().BeFalse();
        }

        [Fact]
        public void NestedScopes_KeepTheGateUpUntilTheOutermostDisposes()
        {
            // The reason this is ref-counted rather than a bool: a walk reached from a path
            // that already raised the gate must not lower it when the inner one returns.
            var outer = ModelWalkGate.Enter("outer walk");
            var inner = ModelWalkGate.Enter("inner walk");

            ModelWalkGate.IsActive.Should().BeTrue();

            inner.Dispose();
            ModelWalkGate.IsActive.Should().BeTrue("the outer walk is still running");

            outer.Dispose();
            ModelWalkGate.IsActive.Should().BeFalse();
        }

        [Fact]
        public void DisposingTheSameScopeTwice_DoesNotLowerTheGateEarly()
        {
            // A double dispose (using + an explicit Dispose on an error path) must not
            // decrement twice and release a gate another walk still owns.
            var outer = ModelWalkGate.Enter("outer walk");
            var inner = ModelWalkGate.Enter("inner walk");

            inner.Dispose();
            inner.Dispose();

            ModelWalkGate.IsActive.Should().BeTrue("the outer walk still holds the gate");

            outer.Dispose();
            ModelWalkGate.IsActive.Should().BeFalse();
        }

        [Fact]
        public void CountSkippedTick_IsSafeWhenTheGateIsDown()
        {
            // AddinTickGate only counts while the gate is up, but the census must never be
            // the thing that throws inside a timer tick.
            Action act = () =>
            {
                ModelWalkGate.CountSkippedTick("test.site");
                ModelWalkGate.CountSkippedTick(null!);
                ModelWalkGate.CountSkippedTick("");
            };

            act.Should().NotThrow();
            ModelWalkGate.IsActive.Should().BeFalse();
        }

        [Fact]
        public void CountSkippedTick_ToleratesConcurrentCallers()
        {
            // The ticks are all UI-thread today, but the census is shared static state and
            // Enter/Dispose can be reached from a background continuation.
            using (ModelWalkGate.Enter("concurrent walk"))
            {
                var work = new List<Task>();
                for (int worker = 0; worker < 4; worker++)
                {
                    string site = $"site{worker}";
                    work.Add(Task.Run(() =>
                    {
                        for (int i = 0; i < 250; i++) ModelWalkGate.CountSkippedTick(site);
                    }));
                }

                Action act = () => Task.WaitAll(work.ToArray());
                act.Should().NotThrow();
                ModelWalkGate.IsActive.Should().BeTrue();
            }

            ModelWalkGate.IsActive.Should().BeFalse();
        }

        [Fact]
        public void AddinTickGate_DoesNotSkip_WhenNothingIsSuspended()
        {
            AddinTickGate.ShouldSkip("test.site").Should().BeFalse();
        }

        [Fact]
        public void AddinTickGate_Skips_WhileAWalkIsRunning()
        {
            using (ModelWalkGate.Enter("walk"))
            {
                AddinTickGate.ShouldSkip("Validation.WindowMonitor").Should().BeTrue(
                    "a tick that runs during a walk re-enters SCAPI mid-walk");
            }

            AddinTickGate.ShouldSkip("Validation.WindowMonitor").Should().BeFalse();
        }
    }
}
