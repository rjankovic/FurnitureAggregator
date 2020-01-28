CREATE PROCEDURE [Stg].[SetExtractEndStatus]
	@extractId INT,
	@status NVARCHAR(30)
AS
	UPDATE Stg.Extracts SET ExtractStatus = @status, EndTime = GETDATE() WHERE ExtractId = @extractId
