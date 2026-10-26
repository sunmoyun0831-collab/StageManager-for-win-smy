using StageManager.Settings;
using Xunit;

namespace StageManager.Tests
{
	public class AppGroupsTests
	{
		[Fact]
		public void Ungrouped_App_Is_A_Group_Of_Its_Own()
		{
			var groups = new AppGroups();

			Assert.Equal(new[] { "Code" }, groups.GroupOf("Code"));
			Assert.Equal("Code", groups.CanonicalKey("Code"));
		}

		[Fact]
		public void Join_Groups_Apps_Case_Insensitive()
		{
			var groups = new AppGroups();

			groups.Join(new[] { "Code", "explorer" });

			Assert.True(groups.AreGrouped("code", "EXPLORER"));
			Assert.Equal(groups.CanonicalKey("Code"), groups.CanonicalKey("explorer"));
		}

		[Fact]
		public void Join_Merges_Existing_Groups()
		{
			var groups = new AppGroups();
			groups.Join(new[] { "a", "b" });
			groups.Join(new[] { "c", "d" });

			groups.Join(new[] { "b", "c" });

			Assert.Equal(new[] { "a", "b", "c", "d" }, groups.GroupOf("a").OrderBy(k => k));
			Assert.Single(groups.ToList());
		}

		[Fact]
		public void Join_Of_A_Single_Unknown_App_Creates_No_Group()
		{
			var groups = new AppGroups();

			groups.Join(new[] { "a" });

			Assert.Empty(groups.ToList());
		}

		[Fact]
		public void Detach_Removes_App_And_Dissolves_Groups_Of_One()
		{
			var groups = new AppGroups(new[] { new[] { "a", "b", "c" } });

			groups.Detach("a");
			Assert.False(groups.AreGrouped("a", "b"));
			Assert.True(groups.AreGrouped("b", "c"));

			groups.Detach("b");
			Assert.Empty(groups.ToList());
		}

		[Fact]
		public void Detach_Of_Ungrouped_App_Does_Nothing()
		{
			var groups = new AppGroups(new[] { new[] { "a", "b" } });

			groups.Detach("x");

			Assert.Equal(new[] { new[] { "a", "b" }.ToList() }, groups.ToList());
		}

		[Fact]
		public void Overlapping_Groups_From_Settings_Are_Merged()
		{
			var groups = new AppGroups(new[] { new[] { "a", "b" }, new[] { "B", "c" } });

			Assert.Single(groups.ToList());
			Assert.True(groups.AreGrouped("a", "c"));
		}
	}
}
