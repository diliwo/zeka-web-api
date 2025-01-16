using AdminAreaManagement.Core.Entities;
using Xunit;

namespace Domain.UnitTests
{
    public class StaffMemenberTest
    {
        [Fact]
        public void Constructor_ShouldInitializeProperties_WhenValidArgumentsProvided()
        {
            // Arrange
            var team = new Team { Name = "Development" };
            var firstName = "John";
            var lastName = "Doe";
            var userName = "johndoe";

            // Act
            var staffMember = new StaffMember(firstName, lastName, team, userName);

            // Assert
            Assert.Equal(firstName, staffMember.FirstName);
            Assert.Equal(lastName, staffMember.LastName);
            Assert.Equal(userName, staffMember.UserName);
            Assert.Equal(team, staffMember.Team);
        }

        [Fact]
        public void Constructor_ShouldInitializeUserNameAsEmpty_WhenNotProvided()
        {
            // Arrange
            var firstName = "John";
            var lastName = "Doe";
            var team = new Team { Name = "Development" };

            // Act
            var staffMember = new StaffMember(firstName, lastName, team);

            // Assert
            Assert.Equal("", staffMember.UserName);
        }

        [Theory]
        [InlineData(null, "Doe", "Development")]
        [InlineData("John", null, "Development")]
        [InlineData("John", "Doe", null)]
        public void Constructor_ShouldThrowArgumentNullException_WhenArgumentsAreInvalid(string firstName, string lastName, string teamName)
        {
            // Arrange
            var team = teamName != null ? new Team { Name = teamName } : null;

            // Act & Assert
            if (string.IsNullOrEmpty(firstName))
            {
                var exception = Assert.Throws<ArgumentNullException>(() => new StaffMember(firstName, lastName, team));
                Assert.Equal("firstName", exception.ParamName);
            }
            else if (string.IsNullOrEmpty(lastName))
            {
                var exception = Assert.Throws<ArgumentNullException>(() => new StaffMember(firstName, lastName, team));
                Assert.Equal("lastName", exception.ParamName);
            }
            else if (team == null)
            {
                var exception = Assert.Throws<ArgumentNullException>(() => new StaffMember(firstName, lastName, team));
                Assert.Equal("team", exception.ParamName);
            }
        }

        [Fact]
        public void FullName_ShouldReturnCorrectlyFormattedName()
        {
            // Arrange
            var firstName = "John";
            var lastName = "Doe";
            var team = new Team { Name = "Development" };

            var staffMember = new StaffMember(firstName, lastName, team);

            // Act
            var fullName = staffMember.FullName;

            // Assert
            Assert.Equal("Doe John", fullName);
        }

        [Fact]
        public void Properties_ShouldAllowSettingAndGetting()
        {
            // Arrange
            var staffMember = new StaffMember();
            var firstName = "Alice";
            var lastName = "Smith";
            var userName = "alicesmith";
            var team = new Team { Name = "HR" };

            // Act
            staffMember.FirstName = firstName;
            staffMember.LastName = lastName;
            staffMember.UserName = userName;
            staffMember.Team = team;

            // Assert
            Assert.Equal(firstName, staffMember.FirstName);
            Assert.Equal(lastName, staffMember.LastName);
            Assert.Equal(userName, staffMember.UserName);
            Assert.Equal(team, staffMember.Team);
        }
    }
}
