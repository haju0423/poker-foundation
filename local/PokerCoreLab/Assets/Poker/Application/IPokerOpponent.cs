using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>Chooses an intent from its own detached view; the table still validates and applies it.</summary>
    public interface IPokerOpponent
    {
        HandCommand Choose(PokerPlayerView view);
    }
}
