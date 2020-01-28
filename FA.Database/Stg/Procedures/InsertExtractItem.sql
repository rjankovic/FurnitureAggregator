CREATE PROCEDURE [Stg].[InsertExtractItem]
	@extractId INT,
	@itemName NVARCHAR(MAX),
	@content NVARCHAR(MAX),
	@itemPath NVARCHAR(MAX),
	@url NVARCHAR(MAX),
	@pageType NVARCHAR(MAX),
	@originUrl NVARCHAR(MAX)
AS
	INSERT INTO Stg.ExtractItems(ExtractId, ItemName, Content, [ItemPath], [Url], PageType, OriginUrl)
	VALUES(@extractId, @itemName, @content, @itemPath, @url, @pageType, @originUrl)

	SELECT IDENT_CURRENT( 'Stg.ExtractItems' ) ExtractItemId
RETURN 0
