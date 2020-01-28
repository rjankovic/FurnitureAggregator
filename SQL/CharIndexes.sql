

SELECT  
IIF(CHARINDEX(N'Šířka: ', Content) > 0, 
	SUBSTRING(
		Content, 
		CHARINDEX(N'Šířka: ', Content) + LEN(N'Šířka: '), 
		Charindex (N'cm', Content, CHARINDEX(N'Šířka: ', Content) + LEN(N'Šířka: ') ) - (CHARINDEX(N'Šířka: ', Content) + LEN(N'Šířka: '))
		
		), NULL)
		
		FROM stg.ExtractItems


SELECT  
IIF(CHARINDEX(N'"articleFullName__specification">', Content) + LEN(N'"articleFullName__specification">') > 0,
	SUBSTRING(
		Content, 
		CHARINDEX(N'"articleFullName__specification">', Content) + LEN(N'"articleFullName__specification">'), 
		CHARINDEX(N'</span>', Content, CHARINDEX(N'"articleFullName__specification">', Content) + LEN(N'"articleFullName__specification">'))  - (CHARINDEX(N'"articleFullName__specification">', Content) + LEN(N'"articleFullName__specification">')))
		, NULL)


		FROM stg.ExtractItems