using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>Bound to one participant by the authority. No arbitrary-seat or whole-state read API.</summary>
    public interface IPokerSeatPort
    {
        PokerPlayerView Read();
        HandReceipt Submit(HandCommand command);
    }
}
