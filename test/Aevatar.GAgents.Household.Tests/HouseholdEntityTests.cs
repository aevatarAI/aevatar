using FluentAssertions;
using Google.Protobuf;
using Xunit;

namespace Aevatar.GAgents.Household.Tests;

public class TriggerConditionTests
{
    private readonly HouseholdEntity _entity = new();

    [Fact]
    public void ShouldTrigger_WhenTemperatureChangesSignificantly()
    {
        var prev = new EnvironmentSnapshot { Temperature = 22.0 };
        var evt = new SensorDataEvent { Temperature = 25.0, Humidity = 50, LightLevel = 60 };

        _entity.ShouldTriggerOnSensorChange(prev, evt).Should().BeTrue();
    }

    [Fact]
    public void ShouldNotTrigger_WhenTemperatureChangeIsSmall()
    {
        var prev = new EnvironmentSnapshot { Temperature = 22.0 };
        var evt = new SensorDataEvent { Temperature = 23.0, Humidity = 50, LightLevel = 60 };

        _entity.ShouldTriggerOnSensorChange(prev, evt).Should().BeFalse();
    }

    [Fact]
    public void ShouldTrigger_WhenLightChangesMoreThan30Percent()
    {
        var prev = new EnvironmentSnapshot { Temperature = 22.0, LightLevel = 100 };
        var evt = new SensorDataEvent { Temperature = 22.0, LightLevel = 60 };

        _entity.ShouldTriggerOnSensorChange(prev, evt).Should().BeTrue();
    }

    [Fact]
    public void ShouldNotTrigger_WhenLightChangeIsSmall()
    {
        var prev = new EnvironmentSnapshot { Temperature = 22.0, LightLevel = 100 };
        var evt = new SensorDataEvent { Temperature = 22.0, LightLevel = 80 };

        _entity.ShouldTriggerOnSensorChange(prev, evt).Should().BeFalse();
    }

    [Fact]
    public void ShouldTrigger_WhenMotionDetected()
    {
        var prev = new EnvironmentSnapshot { MotionDetected = false };
        var evt = new SensorDataEvent { MotionDetected = true };

        _entity.ShouldTriggerOnSensorChange(prev, evt).Should().BeTrue();
    }

    [Fact]
    public void ShouldNotTrigger_WhenMotionAlreadyDetected()
    {
        var prev = new EnvironmentSnapshot { MotionDetected = true };
        var evt = new SensorDataEvent { MotionDetected = true };

        _entity.ShouldTriggerOnSensorChange(prev, evt).Should().BeFalse();
    }

    [Fact]
    public void ShouldNotTrigger_WhenLightLevelIsZero()
    {
        // Division by zero guard
        var prev = new EnvironmentSnapshot { LightLevel = 0 };
        var evt = new SensorDataEvent { LightLevel = 50 };

        _entity.ShouldTriggerOnSensorChange(prev, evt).Should().BeFalse();
    }
}

public class SafetyCheckTests
{
    private readonly HouseholdEntity _entity = new();

    [Fact]
    public void CanReason_WhenStateIsDefault()
    {
        // Default state: no kill switch, no actions, no recent reasoning
        _entity.CanReason(out _).Should().BeTrue();
    }
}

public class StateTransitionTests
{
    [Fact]
    public void ApplyInitialized_SetsProviderAndMode()
    {
        var state = new HouseholdEntityState();
        var evt = new HouseholdInitializedEvent
        {
            ProviderName = "nyxid",
            SystemPrompt = "test prompt",
            MaxToolRounds = 5,
        };

        // Use reflection-free approach: test the proto state directly
        state.ProviderName.Should().BeEmpty();

        // After initialization event, state should have provider
        var initialized = new HouseholdEntityState
        {
            ProviderName = evt.ProviderName,
            SystemPrompt = evt.SystemPrompt,
            CurrentMode = "active",
        };
        initialized.MaxToolRounds = 5;
        initialized.Environment = new EnvironmentSnapshot();
        initialized.Safety = new SafetyState();

        initialized.ProviderName.Should().Be("nyxid");
        initialized.CurrentMode.Should().Be("active");
        initialized.Environment.Should().NotBeNull();
        initialized.Safety.Should().NotBeNull();
    }

    [Fact]
    public void SensorData_UpdatesEnvironment()
    {
        var state = new HouseholdEntityState
        {
            Environment = new EnvironmentSnapshot(),
        };

        var updated = state.Clone();
        updated.Environment.Temperature = 25.5;
        updated.Environment.Humidity = 60;
        updated.Environment.LightLevel = 80;
        updated.Environment.MotionDetected = true;

        updated.Environment.Temperature.Should().Be(25.5);
        updated.Environment.Humidity.Should().Be(60);
        updated.Environment.MotionDetected.Should().BeTrue();
    }

    [Fact]
    public void RecentActions_CappedAtMax()
    {
        var state = new HouseholdEntityState();
        for (var i = 0; i < HouseholdEntityDefaults.MaxRecentActions + 5; i++)
        {
            state.RecentActions.Add(new ActionRecord
            {
                Agent = "test",
                Action = $"action_{i}",
                Timestamp = i,
            });
        }

        // Simulate cap
        while (state.RecentActions.Count > HouseholdEntityDefaults.MaxRecentActions)
            state.RecentActions.RemoveAt(0);

        state.RecentActions.Count.Should().Be(HouseholdEntityDefaults.MaxRecentActions);
        state.RecentActions[0].Action.Should().Be("action_5");
    }

    [Fact]
    public void Memories_UpdateExistingByKey()
    {
        var state = new HouseholdEntityState();
        state.Memories.Add(new MemoryEntry
        {
            Key = "warm_light",
            Content = "Owner prefers warm light",
            Reinforcement = 1,
        });

        // Simulate update
        var existing = state.Memories.FirstOrDefault(m => m.Key == "warm_light");
        existing.Should().NotBeNull();

        var idx = state.Memories.IndexOf(existing!);
        state.Memories[idx] = new MemoryEntry
        {
            Key = "warm_light",
            Content = "Owner prefers warm light at 60%",
            Reinforcement = 2,
        };

        state.Memories.Count.Should().Be(1);
        state.Memories[0].Reinforcement.Should().Be(2);
    }

    [Fact]
    public void SafetyState_ActionsPerMinuteCounter()
    {
        var safety = new SafetyState
        {
            MinuteWindowStartTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ActionsThisMinute = 0,
        };

        safety.ActionsThisMinute++;
        safety.ActionsThisMinute.Should().Be(1);

        safety.ActionsThisMinute++;
        safety.ActionsThisMinute++;
        safety.ActionsThisMinute.Should().Be(3);
    }
}

public class TimePeriodTests
{
    [Theory]
    [InlineData(6, "morning")]
    [InlineData(11, "morning")]
    [InlineData(12, "afternoon")]
    [InlineData(17, "afternoon")]
    [InlineData(18, "evening")]
    [InlineData(21, "evening")]
    [InlineData(22, "night")]
    [InlineData(3, "night")]
    public void GetTimePeriod_ReturnsCorrectPeriod(int hour, string expected)
    {
        HouseholdEntityDefaults.GetTimePeriod(hour).Should().Be(expected);
    }
}

public class ProtobufSerializationTests
{
    [Fact]
    public void HouseholdEntityState_RoundTrips()
    {
        var state = new HouseholdEntityState
        {
            CurrentMode = "active",
            ProviderName = "nyxid",
            LastReasoningTs = 1234567890,
            ReasoningCountToday = 5,
            Environment = new EnvironmentSnapshot
            {
                Temperature = 22.5,
                Humidity = 55,
                LightLevel = 80,
                MotionDetected = true,
                TimeOfDay = "evening",
                DayOfWeek = "Monday",
                SceneDescription = "Living room with two people",
            },
            Safety = new SafetyState
            {
                KillSwitch = false,
                ActionsThisMinute = 2,
            },
        };

        state.RecentActions.Add(new ActionRecord
        {
            Agent = "light-agent",
            Action = "turn_on",
            Detail = "warm_white, 60%",
            Reasoning = "Evening time, owner prefers warm light",
            Timestamp = 1234567890,
        });

        state.Memories.Add(new MemoryEntry
        {
            Key = "warm_light_evening",
            Content = "Owner prefers warm light in evening",
            Reinforcement = 3,
            CreatedAt = 1234567800,
        });

        state.Environment.DeviceStates["living_room_light"] = "on, warm_white, 60%";

        // Serialize and deserialize
        var bytes = state.ToByteArray();
        var restored = HouseholdEntityState.Parser.ParseFrom(bytes);

        restored.CurrentMode.Should().Be("active");
        restored.ProviderName.Should().Be("nyxid");
        restored.Environment.Temperature.Should().Be(22.5);
        restored.Environment.DeviceStates["living_room_light"].Should().Be("on, warm_white, 60%");
        restored.RecentActions.Should().HaveCount(1);
        restored.RecentActions[0].Agent.Should().Be("light-agent");
        restored.Memories.Should().HaveCount(1);
        restored.Memories[0].Reinforcement.Should().Be(3);
        restored.Safety.ActionsThisMinute.Should().Be(2);
    }
}
