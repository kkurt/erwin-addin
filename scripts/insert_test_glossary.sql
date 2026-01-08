-- Performance Test Data for GLOSSARY table
-- Inserts 14,000 random records with NAME between 10-15 characters

USE Fiba;
GO

-- Clear existing test data (optional - comment out if you want to keep existing data)
-- DELETE FROM [dbo].[GLOSSARY] WHERE [NAME] LIKE 'TST%' OR [NAME] LIKE 'XYZ%' OR [NAME] LIKE 'QWE%';

SET NOCOUNT ON;

DECLARE @i INT = 1;
DECLARE @name VARCHAR(50);
DECLARE @dataType VARCHAR(50);
DECLARE @owner VARCHAR(50);
DECLARE @nameLength INT;
DECLARE @chars VARCHAR(36) = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
DECLARE @prefixes TABLE (prefix VARCHAR(4));
DECLARE @dataTypes TABLE (dt VARCHAR(20));
DECLARE @owners TABLE (own VARCHAR(20));

-- Prefixes for variety
INSERT INTO @prefixes VALUES ('TST_'), ('XYZ_'), ('QWE_'), ('ABC_'), ('DEF_'), ('GHI_'), ('JKL_'), ('MNO_'), ('PQR_'), ('UVW_');

-- Data types
INSERT INTO @dataTypes VALUES ('VARCHAR2(50)'), ('VARCHAR2(100)'), ('VARCHAR2(255)'), ('NUMBER'), ('NUMBER(10)'), ('NUMBER(10,2)'), ('DATE'), ('TIMESTAMP'), ('CLOB'), ('INTEGER');

-- Owners
INSERT INTO @owners VALUES ('SYSTEM'), ('DBA'), ('APP_USER'), ('ADMIN'), ('SERVICE'), ('BATCH'), ('REPORT'), ('ETL'), ('API'), ('TEST');

PRINT 'Starting insert of 14,000 test records...';
PRINT 'Start time: ' + CONVERT(VARCHAR, GETDATE(), 120);

BEGIN TRANSACTION;

WHILE @i <= 14000
BEGIN
    -- Random name length between 10 and 15
    SET @nameLength = 10 + ABS(CHECKSUM(NEWID())) % 6;

    -- Build random name
    SET @name = (SELECT TOP 1 prefix FROM @prefixes ORDER BY NEWID());

    -- Add random characters to reach desired length
    WHILE LEN(@name) < @nameLength
    BEGIN
        SET @name = @name + SUBSTRING(@chars, ABS(CHECKSUM(NEWID())) % 36 + 1, 1);
    END

    -- Random data type
    SET @dataType = (SELECT TOP 1 dt FROM @dataTypes ORDER BY NEWID());

    -- Random owner
    SET @owner = (SELECT TOP 1 own FROM @owners ORDER BY NEWID());

    -- Insert if not exists
    IF NOT EXISTS (SELECT 1 FROM [dbo].[GLOSSARY] WHERE [NAME] = @name)
    BEGIN
        INSERT INTO [dbo].[GLOSSARY] ([NAME], [DATA_TYPE], [OWNER])
        VALUES (@name, @dataType, @owner);
    END

    -- Progress indicator every 1000 records
    IF @i % 1000 = 0
    BEGIN
        PRINT 'Inserted ' + CAST(@i AS VARCHAR) + ' records...';
    END

    SET @i = @i + 1;
END

COMMIT TRANSACTION;

PRINT 'Completed!';
PRINT 'End time: ' + CONVERT(VARCHAR, GETDATE(), 120);

-- Verify count
SELECT 'Total records in GLOSSARY: ' + CAST(COUNT(*) AS VARCHAR) AS Result FROM [dbo].[GLOSSARY];

-- Show sample of inserted data
SELECT TOP 20 [NAME], [DATA_TYPE], [OWNER] FROM [dbo].[GLOSSARY] ORDER BY NEWID();
GO
