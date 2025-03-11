using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;
using ClientManagement.Core.Exceptions;
using ClientManagement.Core.ValueObjects;
using Xunit.Abstractions;
using Language = ClientManagement.Core.ValueObjects.Language;

namespace Domain.UnitTests
{
    public class ClientTest
    {
        private readonly Client clientTest;
        private readonly ITestOutputHelper _output; //for custom test output message

        public ClientTest(ITestOutputHelper output)
        {
            clientTest = new Client("500000-00",
                CivilStatus.Divorced,
                "Robert",
                "NOYCE",
                Gender.Male,
                new DateTime(1980, 3, 15),
                "USA",
                "Belge",
                "898988740",
                new Email("Robert@noyce.com"),
                new Phone("0489602345"),
                new Phone("0478568912"),
                new Language("English"),
                new Language("Français"),
                new Address("70", "Rue Geefs", "1030", "Schaerbeek", "Belgium"),
                "Véronique Rivière"
            );
            _output = output;
        }
        [Fact]
        public void Contructor_FirstnameNull_ThrowException()
        {
            Action action = () =>
                new Client("500000-00",
                    CivilStatus.Divorced,
                    string.Empty, 
                    "Noyce",
                    Gender.Male,
                    new DateTime(1980, 3, 15),
                    "RDC",
                    "Usa",
                    "89072235122",
                    new Email("Robert@noyce.com"),
                    new Phone("0489602345"),
                    new Phone("0412345785"),
                    new Language("English"),
                    new Language("Français"),
                    new Address("70", "Rue Geefs", "1030", "Schaerbeek", "Congo"),
                    "Véronique Rivière"
                );
            Assert.Throws<ArgumentNullException>(action);
        }

        [Fact]
        public void Contructor_LastNameNull_ThrowException()
        {
            Action action = () =>
                new Client("500000-00",
                    CivilStatus.Divorced,
                    "Robert",
                    string.Empty,
                    Gender.Male,
                    new DateTime(1980, 3, 15),
                    "France",
                    "Usa",
                    "89072235122",
                    new Email("Robert@noyce.com"),
                    new Phone("0489602345"),
                    new Phone("0489602345"),
                    new Language("English"),
                    new Language("Français"),
                    new Address("70", "Rue Geefs", "1030", "Schaerbeek", "Thailand"),
                    "Véronique Rivière"
                );
            Assert.Throws<ArgumentNullException>(action);
        }

        [Fact]
        public void Contructor_FormatNiss_ThrowException()
        {
            Action action = () =>
                new Client("500000-00",
                    CivilStatus.Divorced,
                    "Robert",
                    "Noyce",
                    Gender.Male,
                    new DateTime(1980, 3, 15),
                    "Mexico",
                    "Belge",
                    "89898874x",
                    new Email("Robert@noyce.com"),
                    new Phone("0489602345"),
                    new Phone("0489602784"),
                    new Language("English"),
                    new Language("Français"),
                    new Address("70", "Rue Geefs", "1030", "Schaerbeek", "Belgium"),
                    "Véronique Rivière"
                );
            Assert.Throws<InvalidNissFormatException>(action);
        }

        [Fact]
        public void HaveFullNameStartingWithLastName()
        {
            Assert.StartsWith("Noyce", clientTest.FullName, StringComparison.CurrentCultureIgnoreCase);
        }


        [Fact]
        public void HaveFullNameEndingWithFirstName()
        {
            Assert.EndsWith("Robert", clientTest.FullName, StringComparison.CurrentCultureIgnoreCase);
        }

        [Fact]
        public void ListOfSupportshouldNotBeNull()
        {
            Assert.NotNull(clientTest.Supports);
        }

        [Fact]
        public void ListOfSchoolRegistrationsShouldNotBeNull()
        {
            _output.WriteLine("Test if null");
            Assert.NotNull(clientTest.SchoolRegistrations);
        }


    }
}
