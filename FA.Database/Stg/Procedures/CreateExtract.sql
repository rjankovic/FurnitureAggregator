CREATE PROCEDURE [Stg].[CreateExtract]
	@type NVARCHAR(30)
AS
	INSERT INTO stg.Extracts(ExtractType, ExtractStatus, StartTime) VALUES(@type, N'RUNNING', GETDATE())
	SELECT IDENT_CURRENT( 'Stg.Extracts' ) ExtractId

RETURN 0
