using System;
using System.Collections.Generic;
using System.Xml.Serialization;

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
        private readonly UserRoutingMode _singleMode;
        private readonly UserRoutingMode _multiplayerMode;
        private readonly UserRoutingMode _fourPlayerMode;

        public UserCommandRouter(
            string[] singleDefaultRoutes,
            IList<UserOverridePreference> singleOverrides,
            string[] multiplayerDefaultRoutes,
            IList<UserOverridePreference> multiplayerOverrides,
            string[] fourPlayerDefaultRoutes,
            IList<UserOverridePreference> fourPlayerOverrides
        )
        {
            _singleMode = new UserRoutingMode(singleDefaultRoutes, singleOverrides);
            _multiplayerMode = new UserRoutingMode(
                multiplayerDefaultRoutes,
                multiplayerOverrides
            );
            _fourPlayerMode = new UserRoutingMode(
                fourPlayerDefaultRoutes,
                fourPlayerOverrides
            );
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
                return normalizedUser == null ||
                    _singleMode.Resolve(normalizedUser) == PlayerTargets.Player1
                        ? PlayerTargets.Player1
                        : PlayerTargets.None;
            }

            if (normalizedUser == null)
            {
                return PlayerTargets.None;
            }

            return playerCount == 2
                ? _multiplayerMode.Resolve(normalizedUser)
                : _fourPlayerMode.Resolve(normalizedUser);
        }

        private static string NormalizeUser(string user)
        {
            return string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        }
    }

    internal sealed class UserRoutingMode
    {
        private readonly UserPatternList[] _defaultRoutes;
        private readonly Dictionary<string, int> _overrides;

        public UserRoutingMode(
            string[] defaultRoutes,
            IList<UserOverridePreference> overrides
        )
        {
            if (defaultRoutes == null || defaultRoutes.Length == 0)
            {
                throw new FormatException("at least one default route is required");
            }

            _defaultRoutes = new UserPatternList[defaultRoutes.Length];
            for (int i = 0; i < defaultRoutes.Length; i++)
            {
                _defaultRoutes[i] = new UserPatternList(defaultRoutes[i]);
            }

            _overrides = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase
            );
            if (overrides == null)
            {
                return;
            }

            for (int i = 0; i < overrides.Count; i++)
            {
                UserOverridePreference item = overrides[i];
                string user = UserOverrideEditor.NormalizeUser(
                    item == null ? null : item.Name
                );
                int playerNumber = item == null ? 0 : item.Player;
                if (user == null || playerNumber < 1 ||
                    playerNumber > _defaultRoutes.Length)
                {
                    throw new FormatException("invalid user override");
                }

                if (_overrides.ContainsKey(user))
                {
                    throw new FormatException(
                        "duplicate user override: " + user
                    );
                }

                _overrides.Add(user, playerNumber);
            }
        }

        public PlayerTargets Resolve(string user)
        {
            int playerNumber;
            if (_overrides.TryGetValue(user, out playerNumber))
            {
                return (PlayerTargets)(1 << (playerNumber - 1));
            }

            for (int i = 0; i < _defaultRoutes.Length; i++)
            {
                if (_defaultRoutes[i].IsMatch(user))
                {
                    return (PlayerTargets)(1 << i);
                }
            }

            return PlayerTargets.None;
        }
    }

    public class UserOverridePreference
    {
        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlAttribute("player")]
        public int Player { get; set; }
    }

    internal static class UserOverrideEditor
    {
        public static bool TryAssign(
            IList<UserOverridePreference> overrides,
            int playerCount,
            int playerNumber,
            string user
        )
        {
            string normalizedUser = NormalizeUser(user);
            if (overrides == null || playerCount < 1 ||
                playerNumber < 1 || playerNumber > playerCount ||
                normalizedUser == null)
            {
                return false;
            }

            for (int i = overrides.Count - 1; i >= 0; i--)
            {
                UserOverridePreference item = overrides[i];
                if (item != null && string.Equals(
                    item.Name,
                    normalizedUser,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    overrides.RemoveAt(i);
                }
            }

            overrides.Add(new UserOverridePreference
            {
                Name = normalizedUser,
                Player = playerNumber
            });
            return true;
        }

        internal static string NormalizeUser(string user)
        {
            if (string.IsNullOrWhiteSpace(user))
            {
                return null;
            }

            string normalized = user.Trim();
            return normalized.IndexOf(',') >= 0 ||
                normalized.IndexOf('*') >= 0 ||
                normalized.IndexOf('[') >= 0 ||
                normalized.IndexOf(']') >= 0
                    ? null
                    : normalized;
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

        public bool IsExactMatch(string user)
        {
            return _kind == PatternKind.Exact &&
                string.Equals(_value, user, StringComparison.OrdinalIgnoreCase);
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
