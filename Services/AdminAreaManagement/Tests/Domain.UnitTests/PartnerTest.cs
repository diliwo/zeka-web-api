using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.ValueObjects;
using Domain.UnitTests.Common;
using FluentAssertions;
using Xunit;

namespace Domain.UnitTests;

public class PartnerTest
{
    private const int testPartnerNumber = 1;
    private readonly Address testAddress = new Address("55", "rue Jeanne d'arc","784512","Paris");
    private readonly StaffMember testJobCoach = new StaffMember("Marie", "Louis", new Team("Imigration Service", "BF"));

    private class TestClass
    {
        public List<ContactPerson> ContactPersons { get; set; } = new();

        public void AssignContactPerson(ContactPerson contactPerson)
        {
            var exists = this.ContactPersons.Contains(contactPerson);

            if (!exists)
            {
                this.ContactPersons.Add(contactPerson);
            }
        }

        public void RemoveContactPerson(List<ContactPerson> contactPerson)
        {
            foreach (var contact in this.ContactPersons.ToList())
            {
                var exists = contactPerson.Contains(contact);

                if (!exists)
                {
                    this.ContactPersons.Remove(contact);
                }
            }
        }
    }

    [Fact]
    public void Constructor_NameNotNull_ThrowException()
    {
        FluentActions.Invoking(
            () => new Partner(
                testPartnerNumber,
                String.Empty,
                testAddress,
                testJobCoach,
                CategoryOfPartner.Public,
                StatusOfPartner.Active,
                DateTimeProvider.Today,
                "Ok")
        ).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AssignContactPerson_ShouldAddIfNotExists()
    {
        // Arrange
        var testClass = new TestClass();
        var contactPerson = new ContactPerson("123-456", "John Rambo", Gender.Male);

        // Act
        testClass.AssignContactPerson(contactPerson);

        // Assert
        Assert.Single(testClass.ContactPersons);
        Assert.Contains(contactPerson, testClass.ContactPersons);
    }

    [Fact]
    public void AssignContactPerson_ShouldNotAddIfExists()
    {
        // Arrange
        var testClass = new TestClass();
        var contactPerson = new ContactPerson("123-456", "Mary Ripley", Gender.Female);
        testClass.ContactPersons.Add(contactPerson);

        // Act
        testClass.AssignContactPerson(contactPerson);

        // Assert
        Assert.Single(testClass.ContactPersons);
    }

    [Fact]
    public void RemoveContactPerson_ShouldRemoveIfNotInList()
    {
        // Arrange
        var testClass = new TestClass();
        var contact1 = new ContactPerson("123-456", "Edmon Lafayette", Gender.Male);
        var contact2 = new ContactPerson("789-012", "Jane Diane", Gender.Female);
        testClass.ContactPersons.AddRange(new[] { contact1, contact2 });

        var retainList = new List<ContactPerson> { contact1 };

        // Act
        testClass.RemoveContactPerson(retainList);

        // Assert
        Assert.Single(testClass.ContactPersons);
        Assert.Contains(contact1, testClass.ContactPersons);
        Assert.DoesNotContain(contact2, testClass.ContactPersons);
    }

    [Fact]
    public void RemoveContactPerson_ShouldNotRemoveIfInList()
    {
        // Arrange
        var testClass = new TestClass();
        var contact1 = new ContactPerson("123-456", "John Doe", Gender.Male);
        var contact2 = new ContactPerson("789-012", "Jane Doe", Gender.Male);
        testClass.ContactPersons.AddRange(new[] { contact1, contact2 });

        var retainList = new List<ContactPerson> { contact1, contact2 };

        // Act
        testClass.RemoveContactPerson(retainList);

        // Assert
        Assert.Equal(2, testClass.ContactPersons.Count);
        Assert.Contains(contact1, testClass.ContactPersons);
        Assert.Contains(contact2, testClass.ContactPersons);
    }
}