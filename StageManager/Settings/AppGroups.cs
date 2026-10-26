using System;
using System.Collections.Generic;
using System.Linq;

namespace StageManager.Settings
{
	/// <summary>
	/// Remembers which apps the user combined into one scene, keyed by the window group key (process name).
	/// An app that is in no group is a scene of its own.
	/// </summary>
	public class AppGroups
	{
		private readonly List<HashSet<string>> _groups = new List<HashSet<string>>();

		public AppGroups(IEnumerable<IEnumerable<string>>? groups = null)
		{
			foreach (var group in groups ?? Enumerable.Empty<IEnumerable<string>>())
				Join(group);
		}

		/// <summary>
		/// Returns all keys grouped with the given key, including the key itself.
		/// </summary>
		public IReadOnlyCollection<string> GroupOf(string key)
		{
			return (IReadOnlyCollection<string>?)Find(key) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
		}

		/// <summary>
		/// Returns a stable key that is equal for all apps of the same group.
		/// </summary>
		public string CanonicalKey(string key)
		{
			var group = Find(key);
			return group is null ? key : group.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).First();
		}

		public bool AreGrouped(string key, string otherKey) => GroupOf(key).Contains(otherKey);

		/// <summary>
		/// Puts all given keys (and everything already grouped with them) into one group.
		/// </summary>
		public void Join(IEnumerable<string> keys)
		{
			var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
			{
				var existing = Find(key);
				if (existing is not null)
				{
					_groups.Remove(existing);
					merged.UnionWith(existing);
				}

				merged.Add(key);
			}

			if (merged.Count > 1)
				_groups.Add(merged);
		}

		/// <summary>
		/// Removes a key from its group so that the app becomes a scene of its own again.
		/// </summary>
		public void Detach(string key)
		{
			var group = Find(key);
			if (group is null)
				return;

			group.Remove(key);

			if (group.Count < 2)
				_groups.Remove(group);
		}

		public List<List<string>> ToList() => _groups.Select(g => g.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()).ToList();

		private HashSet<string>? Find(string key) => _groups.FirstOrDefault(g => g.Contains(key));
	}
}
