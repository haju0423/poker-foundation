using Poker.Foundation;
using Poker.Presentation;

namespace Poker.Application
{
    /// <summary>Bound to one participant by the authority. No arbitrary-seat or whole-state read API.</summary>
    public interface IPokerSeatPort
    {
        PokerPlayerView Read();
        /// <summary>
        /// Return only the bound authority's receipt for this exact command. Throw when correlation is uncertain.
        /// Matching identifiers alone are not authentication or proof that a different authority applied the command.
        /// </summary>
        HandReceipt Submit(HandCommand command);
    }
}
