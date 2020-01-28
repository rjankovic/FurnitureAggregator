CREATE TABLE [Stg].[ExtractItemAttributes]
(
	[ExtractItemAttributeId] INT NOT NULL IDENTITY(1,1) CONSTRAINT PK_Stg_ExtractItemAttributes PRIMARY KEY,
	[ExtractItemId] INT NOT NULL CONSTRAINT FK_Stg_ExtractItemAttributes_ExtractItemId FOREIGN KEY REFERENCES Stg.ExtractItems(ExtractItemId),
	AttributeCategory NVARCHAR(MAX) NOT NULL,
	[AttributeName] NVARCHAR(MAX) NOT NULL,
	[AttributeValue] NVARCHAR(MAX) NOT NULL,
)
