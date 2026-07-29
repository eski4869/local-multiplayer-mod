using System;
using System.Collections.Generic;

namespace LocalMultiplayerMod
{
    [Flags]
    internal enum PlayerTargets
    {
        None = 0,
        Player1 = 1,
        Player2 = 2,
        Player3 = 4,
        Player4 = 8
    }

    internal sealed class UserCommandRouter
    {
        private readonly UserPatternList _singlePlayer1;
        private readonly UserPatternList _multiplayerPlayer1;
        private readonly UserPatternList _multiplayerPlayer2;
        private readonly UserPatternList _fourPlayer1;
        private readonly UserPatternList _fourPlayer2;
        private readonly UserPatternList _fourPlayer3;
        private readonly UserPatternList _fourPlayer4;

        public UserCommandRouter(
            string singlePlayer1,
            string multiplayerPlayer1,
            string multiplayerPlayer2
        ) : this(
            singlePlayer1,
            multiplayerPlayer1,
            multiplayerPlayer2,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty
        )
        {
        }

        public UserCommandRouter(
            string singlePlayer1,
            string multiplayerPlayer1,
            string multiplayerPlayer2,
            string fourPlayer1,
            string fourPlayer2,
            string fourPlayer3,
            string fourPlayer4
        )
        {
            _singlePlayer1 = new UserPatternList(singlePlayer1);
            _multiplayerPlayer1 = new UserPatternList(multiplayerPlayer1);
            _multiplayerPlayer2 = new UserPatternList(multiplayerPlayer2);
            _fourPlayer1 = new UserPatternList(fourPlayer1);
            _fourPlayer2 = new UserPatternList(fourPlayer2);
            _fourPlayer3 = new UserPatternList(fourPlayer3);
            _fourPlayer4 = new UserPatternList(fourPlayer4);
        }

        public PlayerTargets Resolve(bool multiplayerEnabled, string user)
        {
            return Resolve(multiplayerEnabled ? 2 : 1, user);
        }

        public PlayerTargets Resolve(int playerCount, string user)
        {
            string normalizedUser = NormalizeUser(user);

            if (playerCount == 1)
            {
                if (normalizedUser == null || _singlePlayer1.IsMatch(normalizedUser))
                {
                    return PlayerTargets.Player1;
                }

                return PlayerTargets.None;
            }

            if (normalizedUser == null)
            {
                return PlayerTargets.None;
            }

            PlayerTargets targets = PlayerTargets.None;

            if (playerCount == 2)
            {
                if (_multiplayerPlayer1.IsMatch(normalizedUser))
                {
                    targets |= PlayerTargets.Player1;
                }

                if (_multiplayerPlayer2.IsMatch(normalizedUser))
                {
                    targets |= PlayerTargets.Player2;
                }

                return targets;
            }

            if (_fourPlayer1.IsMatch(normalizedUser))
            {
                targets |= PlayerTargets.Player1;
            }

            if (_fourPlayer2.IsMatch(normalizedUser))
            {
                targets |= PlayerTargets.Player2;
            }

            if (_fourPlayer3.IsMatch(normalizedUser))
            {
                targets |= PlayerTargets.Player3;
            }

            if (_fourPlayer4.IsMatch(normalizedUser))
            {
                targets |= PlayerTargets.Player4;
            }

            return targets;
        }

        private static string NormalizeUser(string user)
        {
            return string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        }
    }

    internal sealed class UserPatternList
    {
        private readonly UserPattern[] _patterns;

        public UserPatternList(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _patterns = new UserPattern[0];
                return;
            }

            string[] parts = text.Split(',');
            var patterns = new List<UserPattern>();

            for (int i = 0; i < parts.Length; i++)
            {
                string pattern = parts[i].Trim();
                if (pattern.Length > 0)
                {
                    patterns.Add(UserPattern.Parse(pattern));
                }
            }

            _patterns = patterns.ToArray();
        }

        public bool IsMatch(string user)
        {
            for (int i = 0; i < _patterns.Length; i++)
            {
                if (_patterns[i].IsMatch(user))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class UserPattern
    {
        private enum PatternKind
        {
            Any,
            Exact,
            Prefix,
            InitialRange
        }

        private readonly PatternKind _kind;
        private readonly string _value;
        private readonly char _rangeStart;
        private readonly char _rangeEnd;

        private UserPattern(PatternKind kind, string value, char rangeStart, char rangeEnd)
        {
            _kind = kind;
            _value = value;
            _rangeStart = rangeStart;
            _rangeEnd = rangeEnd;
        }

        public static UserPattern Parse(string pattern)
        {
            if (pattern == "*")
            {
                return new UserPattern(PatternKind.Any, null, '\0', '\0');
            }

            if (IsInitialRange(pattern))
            {
                char start = char.ToLowerInvariant(pattern[1]);
                char end = char.ToLowerInvariant(pattern[3]);

                if (start > end)
                {
                    throw new FormatException("user range must be ascending: " + pattern);
                }

                return new UserPattern(PatternKind.InitialRange, null, start, end);
            }

            int wildcardIndex = pattern.IndexOf('*');
            if (wildcardIndex >= 0)
            {
                if (wildcardIndex != pattern.Length - 1 || pattern.LastIndexOf('*') != wildcardIndex)
                {
                    throw new FormatException("user wildcard is only allowed at the end: " + pattern);
                }

                return new UserPattern(
                    PatternKind.Prefix,
                    pattern.Substring(0, pattern.Length - 1),
                    '\0',
                    '\0'
                );
            }

            if (pattern.IndexOf('[') >= 0 || pattern.IndexOf(']') >= 0)
            {
                throw new FormatException("invalid user range: " + pattern);
            }

            return new UserPattern(PatternKind.Exact, pattern, '\0', '\0');
        }

        public bool IsMatch(string user)
        {
            switch (_kind)
            {
                case PatternKind.Any:
                    return true;
                case PatternKind.Exact:
                    return string.Equals(_value, user, StringComparison.OrdinalIgnoreCase);
                case PatternKind.Prefix:
                    return user.StartsWith(_value, StringComparison.OrdinalIgnoreCase);
                case PatternKind.InitialRange:
                    if (user.Length == 0)
                    {
                        return false;
                    }

                    char initial = char.ToLowerInvariant(user[0]);
                    return initial >= _rangeStart && initial <= _rangeEnd;
                default:
                    return false;
            }
        }

        private static bool IsInitialRange(string pattern)
        {
            return pattern.Length == 6 &&
                pattern[0] == '[' &&
                pattern[2] == '-' &&
                pattern[4] == ']' &&
                pattern[5] == '*';
        }
    }
}
