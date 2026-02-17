-- GLOSSARY Table Setup and Seed Data
-- Table structure: ID, NAME, DATA_TYPE, OWNER, DB_TYPE, KVKK, PCIDSS, CLASSIFICATON

USE Glossary;
GO

-- Create/recreate GLOSSARY table
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[GLOSSARY]') AND type in (N'U'))
BEGIN
    DROP TABLE [dbo].[GLOSSARY];
    PRINT 'Dropped existing GLOSSARY table';
END
GO

CREATE TABLE [dbo].[GLOSSARY] (
    [ID]             INT IDENTITY(1,1) PRIMARY KEY,
    [NAME]           VARCHAR(50)  NOT NULL,
    [DATA_TYPE]      VARCHAR(50)  NOT NULL,
    [OWNER]          VARCHAR(50)  NOT NULL,
    [DB_TYPE]        VARCHAR(50)  NOT NULL,
    [KVKK]           BIT          NOT NULL,
    [PCIDSS]         BIT          NOT NULL,
    [CLASSIFICATON]  VARCHAR(50)  NULL
);

CREATE INDEX IX_GLOSSARY_NAME ON [dbo].[GLOSSARY]([NAME]);

PRINT 'GLOSSARY table created';
GO

-- ============================================================
-- Seed Data (real records)
-- ============================================================
SET IDENTITY_INSERT [dbo].[GLOSSARY] ON;

INSERT INTO [dbo].[GLOSSARY] ([ID], [NAME], [DATA_TYPE], [OWNER], [DB_TYPE], [KVKK], [PCIDSS], [CLASSIFICATON])
VALUES
    (14001, 'TEST',       'VARCHAR(250)',  'Kursat', 'Oracle',1, 0,  N'Kurum İçi'),
    (14002, 'TEST_ADRES', 'VARCHAR(250)',  'Kursat', 'Oracle', 1, 0, N'Kurum İçi'),
    (14006, 'Event',      'VARCHAR2(255)', 'Kursat', 'Oracle', 1, 0, N'Kurum İçi'),
    (14008, 'MUSTERI_NO', 'VARCHAR2(50)',  'Ahmet',  'Oracle', 1, 0, N'Kurum İçi'),
    (14009, 'HESAP_NO',   'VARCHAR2(50)',  'Emre',   'Oracle', 1, 0, N'Kurum İçi'),
    (14010, 'KKR_NO',     'VARCHAR2(50)',  'Kursat', 'Oracle', 1, 1, N'Gizli'),
    (14011, 'GSM_NO',     'VARCHAR2(50)',  'Emre',   'Oracle', 1, 0, N'Hizmete Özel');

SET IDENTITY_INSERT [dbo].[GLOSSARY] OFF;

PRINT 'Inserted 7 seed records';
GO

-- ============================================================
-- Performance Test Data (14,000 random records)
-- ============================================================
SET NOCOUNT ON;

DECLARE @i INT = 1;
DECLARE @name VARCHAR(50);
DECLARE @dataType VARCHAR(50);
DECLARE @owner VARCHAR(50);
DECLARE @dbType VARCHAR(50);
DECLARE @kvkk BIT;
DECLARE @pcidss BIT;
DECLARE @classification VARCHAR(50);
DECLARE @nameLength INT;
DECLARE @chars VARCHAR(36) = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
DECLARE @prefixes TABLE (prefix VARCHAR(4));
DECLARE @dataTypes TABLE (dt VARCHAR(20));
DECLARE @owners TABLE (own VARCHAR(20));
DECLARE @dbTypes TABLE (dbt VARCHAR(20));
DECLARE @classifications TABLE (cls VARCHAR(20));

-- Prefixes for variety
INSERT INTO @prefixes VALUES ('TST_'), ('XYZ_'), ('QWE_'), ('ABC_'), ('DEF_'), ('GHI_'), ('JKL_'), ('MNO_'), ('PQR_'), ('UVW_');

-- Data types
INSERT INTO @dataTypes VALUES ('VARCHAR2(50)'), ('VARCHAR2(100)'), ('VARCHAR2(255)'), ('NUMBER'), ('NUMBER(10)'), ('NUMBER(10,2)'), ('DATE'), ('TIMESTAMP'), ('CLOB'), ('INTEGER');

-- Owners
INSERT INTO @owners VALUES ('Kursat'), ('Ahmet'), ('Emre'), ('Admin'), ('System');

-- DB Types
INSERT INTO @dbTypes VALUES ('Oracle'), ('SQLServer'), ('PostgreSQL'), ('MySQL');

-- Classifications
INSERT INTO @classifications VALUES (N'Kurum İçi'), (N'Gizli'), (N'Hizmete Özel'), (N'Çok Gizli');

PRINT 'Starting insert of 14,000 test records...';
PRINT 'Start time: ' + CONVERT(VARCHAR, GETDATE(), 120);

BEGIN TRANSACTION;

WHILE @i <= 14000
BEGIN
    -- Random name length between 10 and 15
    SET @nameLength = 10 + ABS(CHECKSUM(NEWID())) % 6;

    -- Build random name with prefix
    SET @name = (SELECT TOP 1 prefix FROM @prefixes ORDER BY NEWID());

    WHILE LEN(@name) < @nameLength
    BEGIN
        SET @name = @name + SUBSTRING(@chars, ABS(CHECKSUM(NEWID())) % 36 + 1, 1);
    END

    -- Random values
    SET @dataType = (SELECT TOP 1 dt FROM @dataTypes ORDER BY NEWID());
    SET @owner = (SELECT TOP 1 own FROM @owners ORDER BY NEWID());
    SET @dbType = (SELECT TOP 1 dbt FROM @dbTypes ORDER BY NEWID());
    SET @kvkk = CASE WHEN ABS(CHECKSUM(NEWID())) % 3 = 0 THEN 1 ELSE 0 END;
    SET @pcidss = CASE WHEN ABS(CHECKSUM(NEWID())) % 4 = 0 THEN 1 ELSE 0 END;
    SET @classification = CASE WHEN ABS(CHECKSUM(NEWID())) % 5 = 0 THEN NULL
                               ELSE (SELECT TOP 1 cls FROM @classifications ORDER BY NEWID()) END;

    -- Insert if name doesn't exist
    IF NOT EXISTS (SELECT 1 FROM [dbo].[GLOSSARY] WHERE [NAME] = @name)
    BEGIN
        INSERT INTO [dbo].[GLOSSARY] ([NAME], [DATA_TYPE], [OWNER], [DB_TYPE], [KVKK], [PCIDSS], [CLASSIFICATON])
        VALUES (@name, @dataType, @owner, @dbType, @kvkk, @pcidss, @classification);
    END

    IF @i % 1000 = 0
        PRINT 'Inserted ' + CAST(@i AS VARCHAR) + ' records...';

    SET @i = @i + 1;
END

COMMIT TRANSACTION;

PRINT 'Completed!';
PRINT 'End time: ' + CONVERT(VARCHAR, GETDATE(), 120);

-- Summary
SELECT 'Total records in GLOSSARY: ' + CAST(COUNT(*) AS VARCHAR) AS Result FROM [dbo].[GLOSSARY];
SELECT TOP 20 * FROM [dbo].[GLOSSARY] ORDER BY [ID];
GO
