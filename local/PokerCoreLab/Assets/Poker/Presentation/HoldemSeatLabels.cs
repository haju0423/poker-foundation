using System;
using System.Collections.Generic;
using System.Globalization;
using Poker.Foundation;

namespace Poker.Presentation
{
    /// <summary>Viewer-facing names only. Actions still address the original SeatId.</summary>
    public sealed class HoldemSeatLabels
    {
        private readonly Dictionary<SeatId, string> labels = new Dictionary<SeatId, string>();

        public HoldemSeatLabels(HoldemTableDisplay table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            var names = new string[table.SeatCount];
            var numbered = new bool[table.SeatCount];
            var resolved = new string[table.SeatCount];
            for (int i = 0; i < names.Length; i++)
            {
                var seat = table.GetSeatAt(i);
                names[i] = !string.IsNullOrEmpty(seat.Name) ? seat.Name
                    : seat.Seat == table.ViewerSeat ? "나"
                    : table.SeatCount == 2 ? "상대" : "상대 " + seat.TableIndex;
                numbered[i] = string.Equals(names[i], "나", StringComparison.Ordinal);
            }
            // Include every seat, even after a fold, elimination or disconnection.
            // Resolve before replacing the viewer's name, so all viewers agree on seat prefixes.
            for (int i = 0; i < names.Length; i++)
                for (int j = i + 1; j < names.Length; j++)
                    if (string.Equals(names[i], names[j], StringComparison.Ordinal))
                        numbered[i] = numbered[j] = true;

            bool changed;
            do
            {
                for (int i = 0; i < names.Length; i++)
                    resolved[i] = numbered[i]
                        ? table.GetSeatAt(i).Seat.Value.ToString(CultureInfo.InvariantCulture) + "번 · " + names[i]
                        : names[i];
                changed = false;
                // A nickname may itself look like "2번 · 민수". Never parse it as an identity.
                // Number that seat too if it collides with a generated label.
                for (int i = 0; i < names.Length; i++)
                    for (int j = i + 1; j < names.Length; j++)
                    {
                        if (!string.Equals(resolved[i], resolved[j], StringComparison.Ordinal)) continue;
                        if (!numbered[i]) { numbered[i] = true; changed = true; }
                        if (!numbered[j]) { numbered[j] = true; changed = true; }
                    }
            } while (changed);

            for (int i = 0; i < names.Length; i++)
            {
                var seat = table.GetSeatAt(i).Seat;
                labels.Add(seat, seat == table.ViewerSeat ? "나" : resolved[i]);
            }
        }

        public string Get(SeatId seat)
        {
            if (labels.TryGetValue(seat, out string label)) return label;
            throw new ArgumentException("Unknown display seat.", nameof(seat));
        }
    }
}
