using Bast.Domain;

namespace Bast.Tests;

public class NaturalStringComparerTests
{
    [Fact]
    public void Orders_folders_numerically()
    {
        string[] names = ["Video 10", "Video 2", "Video 001", "video 3", "Video 100", "Video 21"];
        var ordered = names.OrderBy(n => n, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(["Video 001", "Video 2", "video 3", "Video 10", "Video 21", "Video 100"], ordered);
    }

    [Theory]
    [InlineData("Video 001", 1L)]
    [InlineData("GTA 6 - parte 12", 6L)]
    [InlineData("sem numero", null)]
    public void Extracts_first_number(string name, long? expected) =>
        Assert.Equal(expected, NaturalStringComparer.FirstNumber(name));
}

public class PublicationStateMachineTests
{
    [Fact]
    public void Unknown_cannot_go_straight_back_to_uploading()
    {
        Assert.False(PublicationStateMachine.CanTransition(PublicationState.Unknown, PublicationState.Uploading));
        Assert.True(PublicationStateMachine.CanTransition(PublicationState.Unknown, PublicationState.Published));
        Assert.True(PublicationStateMachine.CanTransition(PublicationState.Unknown, PublicationState.RetryWait));
    }

    [Fact]
    public void Published_is_final()
    {
        foreach (var next in Enum.GetValues<PublicationState>())
        {
            Assert.False(PublicationStateMachine.CanTransition(PublicationState.Published, next));
        }
    }

    [Fact]
    public void Every_state_has_defined_transitions()
    {
        foreach (var state in Enum.GetValues<PublicationState>())
        {
            // Não deve lançar KeyNotFoundException para nenhum estado.
            PublicationStateMachine.CanTransition(state, PublicationState.Cancelled);
        }
    }

    [Fact]
    public void Publication_follows_the_happy_path_and_rejects_invalid_jumps()
    {
        var now = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        var publication = new Publication();

        Assert.Throws<InvalidOperationException>(() => publication.TransitionTo(PublicationState.Published, now));

        publication.TransitionTo(PublicationState.Validating, now);
        publication.TransitionTo(PublicationState.Scheduled, now);
        publication.TransitionTo(PublicationState.Uploading, now);
        publication.TransitionTo(PublicationState.Processing, now);
        publication.TransitionTo(PublicationState.Published, now);

        Assert.Equal(PublicationState.Published, publication.State);
        Assert.Equal(now, publication.CompletedAtUtc);
        Assert.Equal(5, publication.Version);
        Assert.Throws<InvalidOperationException>(() => publication.TransitionTo(PublicationState.Uploading, now));
    }
}
