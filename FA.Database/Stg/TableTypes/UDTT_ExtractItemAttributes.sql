CREATE TYPE [stg].[UDTT_ExtractItemAttributes] AS TABLE
(
	AttributeCategory NVARCHAR(MAX),
	AttributeName NVARCHAR(MAX),
	AttributeValue NVARCHAR(MAX)
)
