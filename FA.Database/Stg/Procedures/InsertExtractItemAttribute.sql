CREATE PROCEDURE [Stg].[InsertExtractItemAttributes]
	@extractItemId INT,
	@attributes [stg].[UDTT_ExtractItemAttributes] READONLY
AS
	INSERT INTO Stg.ExtractItemAttributes(ExtractItemId, AttributeCategory, AttributeName, AttributeValue)
	SELECT @extractItemId, AttributeCategory, AttributeName, AttributeValue
	FROM @attributes

RETURN 0
