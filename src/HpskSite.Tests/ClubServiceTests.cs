using HpskSite.Services;
using Moq;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Unit tests for ClubService
    /// Tests club lookup functionality to prevent regression of club name display issues
    ///
    /// Background: Clubs were migrated from Member Type to Document Type nodes.
    /// ClubService ensures proper lookups without using IMemberService incorrectly.
    ///
    /// Critical for preventing:
    /// - "Club 1098" display instead of actual names
    /// - "Ingen klubb" (No club) errors
    /// - Silent failures when using wrong service
    /// </summary>
    public class ClubServiceTests
    {
        // ============ GetClubNameById Tests ============

        [Fact]
        public void GetClubNameById_WithValidClubId_ReturnsClubName()
        {
            // Arrange
            var clubId = 1098;
            var expectedName = "Helsingborgs Pistolskytteklubb";

            var mockContentService = new Mock<IContentService>();
            var mockContent = new Mock<IContent>();
            var mockContentType = new Mock<ISimpleContentType>();

            mockContentType.Setup(ct => ct.Alias).Returns("club");
            mockContent.Setup(c => c.ContentType).Returns(mockContentType.Object);
            mockContent.Setup(c => c.GetValue<string>("clubName", null, null, false)).Returns(expectedName);
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns(mockContent.Object);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Equal(expectedName, result);
        }

        [Fact]
        public void GetClubNameById_WithInvalidClubId_ReturnsNull()
        {
            // Arrange
            var clubId = 9999;

            var mockContentService = new Mock<IContentService>();
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns((IContent?)null);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void GetClubNameById_WithWrongContentType_ReturnsNull()
        {
            // Arrange
            var clubId = 1098;

            var mockContentService = new Mock<IContentService>();
            var mockContent = new Mock<IContent>();
            var mockContentType = new Mock<ISimpleContentType>();

            // Content exists but is NOT a club (e.g., it's a competition or other type)
            mockContentType.Setup(ct => ct.Alias).Returns("competition");
            mockContent.Setup(c => c.ContentType).Returns(mockContentType.Object);
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns(mockContent.Object);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void GetClubNameById_WithNoClubNameProperty_FallsBackToName()
        {
            // Arrange
            var clubId = 1098;
            var expectedName = "Test Club";

            var mockContentService = new Mock<IContentService>();
            var mockContent = new Mock<IContent>();
            var mockContentType = new Mock<ISimpleContentType>();

            mockContentType.Setup(ct => ct.Alias).Returns("club");
            mockContent.Setup(c => c.ContentType).Returns(mockContentType.Object);
            mockContent.Setup(c => c.GetValue<string>("clubName", null, null, false)).Returns((string?)null);
            mockContent.Setup(c => c.Name).Returns(expectedName);
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns(mockContent.Object);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Equal(expectedName, result);
        }

        [Fact]
        public void GetClubNameById_WithZeroId_ReturnsNull()
        {
            // Arrange
            var clubId = 0;

            var mockContentService = new Mock<IContentService>();
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns((IContent?)null);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void GetClubNameById_WithNegativeId_ReturnsNull()
        {
            // Arrange
            var clubId = -1;

            var mockContentService = new Mock<IContentService>();
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns((IContent?)null);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Null(result);
        }

        // ============ GetClubById Tests ============

        [Fact]
        public void GetClubById_WithValidClubId_ReturnsClubInfo()
        {
            // Arrange
            var clubId = 1098;
            var expectedName = "Helsingborgs Pistolskytteklubb";
            var expectedDescription = "Test description";
            var expectedCity = "Helsingborg";
            var expectedEmail = "info@hpsk.se";

            var mockContentService = new Mock<IContentService>();
            var mockContent = new Mock<IContent>();
            var mockContentType = new Mock<ISimpleContentType>();

            mockContentType.Setup(ct => ct.Alias).Returns("club");
            mockContent.Setup(c => c.ContentType).Returns(mockContentType.Object);
            mockContent.Setup(c => c.Id).Returns(clubId);
            mockContent.Setup(c => c.GetValue<string>("clubName", null, null, false)).Returns(expectedName);
            mockContent.Setup(c => c.GetValue<string>("description", null, null, false)).Returns(expectedDescription);
            mockContent.Setup(c => c.GetValue<string>("city", null, null, false)).Returns(expectedCity);
            mockContent.Setup(c => c.GetValue<string>("contactEmail", null, null, false)).Returns(expectedEmail);
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns(mockContent.Object);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubById(clubId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(clubId, result.Id);
            Assert.Equal(expectedName, result.Name);
            Assert.Equal(expectedDescription, result.Description);
            Assert.Equal(expectedCity, result.City);
            Assert.Equal(expectedEmail, result.ContactEmail);
        }

        [Fact]
        public void GetClubById_WithInvalidClubId_ReturnsNull()
        {
            // Arrange
            var clubId = 9999;

            var mockContentService = new Mock<IContentService>();
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns((IContent?)null);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubById(clubId);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void GetClubById_WithMissingProperties_ReturnsEmptyStrings()
        {
            // Arrange
            var clubId = 1098;

            var mockContentService = new Mock<IContentService>();
            var mockContent = new Mock<IContent>();
            var mockContentType = new Mock<ISimpleContentType>();

            mockContentType.Setup(ct => ct.Alias).Returns("club");
            mockContent.Setup(c => c.ContentType).Returns(mockContentType.Object);
            mockContent.Setup(c => c.Id).Returns(clubId);
            mockContent.Setup(c => c.Name).Returns("Test Club");
            mockContent.Setup(c => c.GetValue<string>("clubName", null, null, false)).Returns((string?)null);
            mockContent.Setup(c => c.GetValue<string>("description", null, null, false)).Returns((string?)null);
            mockContent.Setup(c => c.GetValue<string>("city", null, null, false)).Returns((string?)null);
            mockContent.Setup(c => c.GetValue<string>("contactEmail", null, null, false)).Returns((string?)null);
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns(mockContent.Object);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubById(clubId);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(clubId, result.Id);
            Assert.Equal("Test Club", result.Name); // Falls back to Name
            Assert.Equal("", result.Description);
            Assert.Equal("", result.City);
            Assert.Equal("", result.ContactEmail);
        }

        // ============ ClubInfo Model Tests ============

        [Fact]
        public void ClubInfo_DefaultConstructor_InitializesWithEmptyStrings()
        {
            // Act
            var clubInfo = new ClubInfo();

            // Assert
            Assert.Equal(0, clubInfo.Id);
            Assert.Equal(string.Empty, clubInfo.Name);
            Assert.Equal(string.Empty, clubInfo.Description);
            Assert.Equal(string.Empty, clubInfo.City);
            Assert.Equal(string.Empty, clubInfo.ContactEmail);
        }

        [Fact]
        public void ClubInfo_PropertySetters_WorkCorrectly()
        {
            // Arrange
            var clubInfo = new ClubInfo();

            // Act
            clubInfo.Id = 1098;
            clubInfo.Name = "Test Club";
            clubInfo.Description = "Test Description";
            clubInfo.City = "Test City";
            clubInfo.ContactEmail = "test@example.com";

            // Assert
            Assert.Equal(1098, clubInfo.Id);
            Assert.Equal("Test Club", clubInfo.Name);
            Assert.Equal("Test Description", clubInfo.Description);
            Assert.Equal("Test City", clubInfo.City);
            Assert.Equal("test@example.com", clubInfo.ContactEmail);
        }

        // ============ Regression Prevention Tests ============

        [Fact]
        public void GetClubNameById_PreventsClubIdDisplayRegression()
        {
            // This test documents the bug that was fixed:
            // Before fix: Would return "Club 1098" when lookup failed
            // After fix: Returns null when club not found, allowing caller to handle it

            // Arrange
            var clubId = 1098;

            var mockContentService = new Mock<IContentService>();
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns((IContent?)null);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Null(result);
            // Caller can now handle null appropriately:
            // - Display "Unknown Club"
            // - Log warning
            // - Use fallback logic
            // But NOT display "Club 1098" which confused users
        }

        [Fact]
        public void GetClubNameById_PreventsMemberServiceMisuseRegression()
        {
            // This test documents another bug that was fixed:
            // Before fix: Code used IMemberService.GetById() for clubs
            // After fix: ClubService uses IContentService properly

            // Arrange
            var clubId = 1098;
            var expectedName = "Helsingborgs Pistolskytteklubb";

            var mockContentService = new Mock<IContentService>();
            var mockContent = new Mock<IContent>();
            var mockContentType = new Mock<ISimpleContentType>();

            mockContentType.Setup(ct => ct.Alias).Returns("club");
            mockContent.Setup(c => c.ContentType).Returns(mockContentType.Object);
            mockContent.Setup(c => c.GetValue<string>("clubName", null, null, false)).Returns(expectedName);
            mockContentService.Setup(cs => cs.GetById(clubId)).Returns(mockContent.Object);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Equal(expectedName, result);
            // Verify IContentService was used, NOT IMemberService
            mockContentService.Verify(cs => cs.GetById(clubId), Times.Once);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void GetClubNameById_WithEdgeCaseIds_HandlesGracefully(int clubId)
        {
            // Arrange
            var mockContentService = new Mock<IContentService>();
            mockContentService.Setup(cs => cs.GetById(It.IsAny<int>())).Returns((IContent?)null);

            var mockUmbracoContextAccessor = new Mock<IUmbracoContextAccessor>();
            mockUmbracoContextAccessor.Setup(uca => uca.TryGetUmbracoContext(out It.Ref<IUmbracoContext>.IsAny))
                .Returns(false);

            var clubService = new ClubService(mockUmbracoContextAccessor.Object, mockContentService.Object, null);

            // Act
            var result = clubService.GetClubNameById(clubId);

            // Assert
            Assert.Null(result);
            // Service handles edge cases gracefully without throwing
        }
    }
}
