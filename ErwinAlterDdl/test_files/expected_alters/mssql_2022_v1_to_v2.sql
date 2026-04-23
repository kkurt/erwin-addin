-- ============================================================================
-- MSSQL 2022 - v1 -> v2 ALTER SCRIPT (REFERANS)
-- ============================================================================
-- Bu script v1 baseline veritabani uzerinde calistirildiginda v2 sema
-- durumuna getirir. Test/CC dogrulama icin referans cikti.
--
-- ONEMLI NOT - ERWIN UYUM ETKISI:
-- v1/v2 SQL dosyalari Erwin DM uyumlu olmasi icin "isimsiz constraint"
-- kullaniyor. Bu yuzden gercek deploy edildiginde MSSQL'in atadigi
-- otomatik constraint isimleri (PK__ORDERS__C3905BCFxxx gibi) olusur.
-- Bu script'te DROP CONSTRAINT yapan kismilarda ELLE ISIM CIKARMAK gerekir:
--   - sys.key_constraints / sys.foreign_keys / sys.check_constraints'tan
--     bulup yerine yazmak lazim
--   - Ornegin: DECLARE @fk_name NVARCHAR(128) = (SELECT name FROM
--     sys.foreign_keys WHERE parent_object_id = OBJECT_ID('app.ORDERS')
--     AND referenced_object_id = OBJECT_ID('app.PROMOTION'));
--     EXEC ('ALTER TABLE app.ORDERS DROP CONSTRAINT [' + @fk_name + ']');
--
-- ALTERNATIF: Asagidaki script "isim verilmis senaryo" varsayar; eger
-- isimler farkliysa MSSQL hata mesajlari rehber olur, isimleri degistirmek
-- yeterli.
--
-- Calisma sirasi:
--   1. Bagimliliklari kopar
--   2. Sequence'leri ayarla
--   3. TRANSACTION_LOG drop+create
--   4. PROMOTION drop+create (composite PK icin)
--   5. PRODUCT_ARCHIVE drop
--   6. CUSTOMER_BACKUP rename
--   7. ORDER_ITEM schema move
--   8. Tablolarin kolon/constraint degisiklikleri
--   9. CAMPAIGN yeni tablo
--  10. FK'leri yeniden kur
--  11. Index'ler
--  12. Trigger'lar
--  13. View'ler
--  14. Manuel adimlar (notlar)
-- ============================================================================

SET XACT_ABORT ON;
GO

-- ============================================================================
-- 1. BAGIMLILIKLARI KOPAR
-- ============================================================================

-- View'leri drop et
DROP VIEW app.VW_LEGACY_CUSTOMER;     -- VW-02
DROP VIEW app.VW_ORDER_DETAIL;        -- VW-04 (yeniden create)
DROP VIEW app.VW_CUSTOMER_SUMMARY;    -- VW-03 (yeniden create)
GO

-- Trigger'lari drop et
DROP TRIGGER app.TR_ORDERS_TIMESTAMP; -- TRG-02 (yeniden create)
GO

-- FK'leri drop et (isim varsayimi - gerekirse sys.foreign_keys'tan bul)
-- Asagidaki FK'lerin isimleri Erwin ile import edildiyse otomatik atanmis
-- olur. Production'da bu isimleri tespit edip yerine yazmalisiniz.
ALTER TABLE app.ORDERS         DROP CONSTRAINT FK_ORDERS_PROMOTION;  -- FK-02
ALTER TABLE app.ORDERS         DROP CONSTRAINT FK_ORDERS_CUSTOMER;   -- baseline rebuild
ALTER TABLE sales.ORDER_ITEM   DROP CONSTRAINT FK_ORDER_ITEM_ORDERS; -- FK-03
ALTER TABLE sales.ORDER_ITEM   DROP CONSTRAINT FK_ORDER_ITEM_PRODUCT;-- ORDER_ITEM tasinacak
GO

-- Index'leri drop et
DROP INDEX IX_ORDERS_DATE       ON app.ORDERS;        -- IDX-02
DROP INDEX IX_ORDERS_CUSTOMER   ON app.ORDERS;        -- IDX-04 (kolon ekleme)
DROP INDEX IX_ORDERS_PROMOTION  ON app.ORDERS;        -- FK-02 sonrasi
DROP INDEX IX_TX_LOG            ON app.TRANSACTION_LOG;
DROP INDEX IX_OI_PROD           ON sales.ORDER_ITEM;  -- IDX-03 rename
DROP INDEX IX_CUSTOMER_TAXNO    ON app.CUSTOMER;      -- IDX-06 UNIQUE icin
GO

-- UNIQUE constraint drop (UQ-02)
ALTER TABLE app.CUSTOMER DROP CONSTRAINT UQ_CUSTOMER_TAXNO;
GO

-- ============================================================================
-- 2. SEQUENCE'LER
-- ============================================================================

-- SEQ-02: INCREMENT 1 -> 10
DROP SEQUENCE app.SEQ_ORDER_NUMBER;
GO
CREATE SEQUENCE app.SEQ_ORDER_NUMBER
    AS BIGINT START WITH 1 INCREMENT BY 10 MINVALUE 1 NO MAXVALUE NO CACHE;
GO

-- SEQ-01: yeni
CREATE SEQUENCE app.SEQ_TRANSACTION_LOG
    AS BIGINT START WITH 1 INCREMENT BY 1 MINVALUE 1 NO MAXVALUE CACHE 100;
GO

-- ============================================================================
-- 3. TRANSACTION_LOG: drop+create
-- (PART-01, COL-13, PK-01, FK-01, IDX-05, STO-02 birden uygulaniyor)
-- ============================================================================

DROP TABLE app.TRANSACTION_LOG;
GO

-- Partition function/scheme (manuel olusturulmali; production'da)
-- CREATE PARTITION FUNCTION PF_TXN_DATE (DATETIME2) ...
-- CREATE PARTITION SCHEME PS_TXN_DATE ...

CREATE TABLE app.TRANSACTION_LOG (
    log_id         BIGINT          IDENTITY(1,1) NOT NULL,
    txn_id         BIGINT          NOT NULL,
    customer_id    INT             NULL,
    txn_date       DATETIME2       NOT NULL,
    txn_amount     DECIMAL(18,2)   NOT NULL,
    txn_type       VARCHAR(10)     NOT NULL,
    PRIMARY KEY (log_id, txn_date)
);
GO
-- STO-02 PAGE compression: ALTER TABLE ... REBUILD WITH (DATA_COMPRESSION=PAGE);
-- PART-01 ON PS_TXN_DATE: model uzerinden manuel

-- ============================================================================
-- 4. PROMOTION: drop+create (PART-02, composite PK)
-- ============================================================================

DROP TABLE app.PROMOTION;
GO

CREATE TABLE app.PROMOTION (
    promotion_id    INT             NOT NULL,
    code            VARCHAR(30)     NOT NULL,
    description     VARCHAR(500)    NULL,
    discount_pct    DECIMAL(5,2)    NOT NULL,
    start_date      DATE            NOT NULL,
    end_date        DATE            NOT NULL,
    PRIMARY KEY (promotion_id, start_date)
);
GO
ALTER TABLE app.PROMOTION ADD CHECK (end_date >= start_date);
GO

-- ============================================================================
-- 5. PRODUCT_ARCHIVE drop (TBL-02)
-- ============================================================================
DROP TABLE app.PRODUCT_ARCHIVE;
GO

-- ============================================================================
-- 6. CUSTOMER_BACKUP -> CUSTOMER_HISTORY rename (TBL-03)
-- ============================================================================
EXEC sp_rename N'app.CUSTOMER_BACKUP', N'CUSTOMER_HISTORY';
GO

-- ============================================================================
-- 7. ORDER_ITEM: schema sales -> ops (TBL-04)
-- ============================================================================
CREATE SCHEMA ops AUTHORIZATION dbo;
GO

ALTER SCHEMA ops TRANSFER sales.ORDER_ITEM;
GO

-- ============================================================================
-- 8. CUSTOMER kolon degisiklikleri
-- ============================================================================

-- COL-12 (collation) - manuel: ALTER COLUMN ... COLLATE Turkish_CI_AS

-- COL-05: address VARCHAR(100) -> VARCHAR(250)
ALTER TABLE app.CUSTOMER ALTER COLUMN address VARCHAR(250) NULL;
GO

-- COL-03: mobile_phone -> mobile_no
EXEC sp_rename N'app.CUSTOMER.mobile_phone', N'mobile_no', N'COLUMN';
GO

-- COL-02: fax_number drop
ALTER TABLE app.CUSTOMER DROP COLUMN fax_number;
GO

-- COL-09: status DEFAULT 'ACTIVE' eklenmesi
ALTER TABLE app.CUSTOMER ADD DEFAULT ('ACTIVE') FOR status;
GO

-- COL-01: email_verified yeni kolon
ALTER TABLE app.CUSTOMER ADD email_verified BIT NOT NULL DEFAULT (0);
GO

-- UQ-01: email UNIQUE
ALTER TABLE app.CUSTOMER ADD UNIQUE (email);
GO

-- TBL-05: tablo comment - manuel
-- EXEC sp_addextendedproperty 'MS_Description', N'Musteri ana tablosu', ...

-- ============================================================================
-- 9. ORDERS kolon degisiklikleri
-- ============================================================================

-- CHK-02 eski drop (isim varsayimi)
ALTER TABLE app.ORDERS DROP CONSTRAINT CK_ORDERS_AMOUNT;
GO

-- COL-04: order_amount INT -> BIGINT
ALTER TABLE app.ORDERS ALTER COLUMN order_amount BIGINT NOT NULL;
GO

-- COL-07: customer_id NULL -> NOT NULL
-- (production'da once UPDATE ... WHERE customer_id IS NULL gerekli)
ALTER TABLE app.ORDERS ALTER COLUMN customer_id INT NOT NULL;
GO

-- COL-10: order_status DEFAULT 'NEW' -> 'PENDING'
ALTER TABLE app.ORDERS DROP CONSTRAINT DF_ORDERS_STATUS;
GO
ALTER TABLE app.ORDERS ADD DEFAULT ('PENDING') FOR order_status;
GO

-- CHK-02: yeni CHECK
ALTER TABLE app.ORDERS ADD CHECK (order_amount >= 0 AND order_amount <= 1000000);
GO

-- IDX-07 (NCL -> CL PK swap) - manuel: PK clustered index olarak yeniden olustur

-- COL-15 (comment) - manuel

-- ============================================================================
-- 10. ORDER_ITEM (artik ops schema)
-- ============================================================================

-- COL-11: quantity DEFAULT 1 silme
ALTER TABLE ops.ORDER_ITEM DROP CONSTRAINT DF_ORDER_ITEM_QTY;
GO

-- COL-06: unit_price DECIMAL(18,4) -> DECIMAL(12,2)
ALTER TABLE ops.ORDER_ITEM ALTER COLUMN unit_price DECIMAL(12,2) NOT NULL;
GO

-- COL-08: discount_pct NOT NULL -> NULL
ALTER TABLE ops.ORDER_ITEM ALTER COLUMN discount_pct DECIMAL(5,2) NULL;
GO

-- COL-14: line_total computed column - manuel:
-- ALTER TABLE ops.ORDER_ITEM ADD line_total AS (quantity * unit_price) PERSISTED;

-- PK-02: PK(order_id) -> PK(order_id, line_no)
ALTER TABLE ops.ORDER_ITEM DROP CONSTRAINT PK_ORDER_ITEM;
GO
ALTER TABLE ops.ORDER_ITEM ADD PRIMARY KEY (order_id, line_no);
GO

-- CHK-01: quantity > 0
ALTER TABLE ops.ORDER_ITEM ADD CHECK (quantity > 0);
GO

-- STO-01 filegroup - manuel

-- ============================================================================
-- 11. CAMPAIGN: yeni tablo (TBL-01)
-- ============================================================================

CREATE TABLE app.CAMPAIGN (
    campaign_id           INT             NOT NULL,
    promotion_id          INT             NOT NULL,
    promotion_start_date  DATE            NOT NULL,
    campaign_name         VARCHAR(200)    NOT NULL,
    channel               VARCHAR(20)     NOT NULL,
    launch_date           DATE            NOT NULL,
    budget                DECIMAL(15,2)   NOT NULL,
    PRIMARY KEY (campaign_id, launch_date)
);
GO

ALTER TABLE app.CAMPAIGN ADD CHECK (budget >= 0);
GO

ALTER TABLE app.CAMPAIGN
    ADD FOREIGN KEY (promotion_id, promotion_start_date)
    REFERENCES app.PROMOTION (promotion_id, start_date)
    ON DELETE CASCADE;
GO

-- ============================================================================
-- 12. FK'LERI YENIDEN KUR
-- ============================================================================

-- FK_ORDERS_CUSTOMER (baseline)
ALTER TABLE app.ORDERS
    ADD FOREIGN KEY (customer_id) REFERENCES app.CUSTOMER (customer_id);
GO

-- FK_ORDER_ITEM_ORDERS (FK-03: NO ACTION -> CASCADE)
ALTER TABLE ops.ORDER_ITEM
    ADD FOREIGN KEY (order_id) REFERENCES app.ORDERS (order_id)
    ON DELETE CASCADE;
GO

-- FK_ORDER_ITEM_PRODUCT (baseline)
ALTER TABLE ops.ORDER_ITEM
    ADD FOREIGN KEY (product_id) REFERENCES app.PRODUCT (product_id);
GO

-- FK_TX_LOG_CUSTOMER (FK-01 yeni)
ALTER TABLE app.TRANSACTION_LOG
    ADD FOREIGN KEY (customer_id) REFERENCES app.CUSTOMER (customer_id);
GO

-- ============================================================================
-- 13. INDEX'LER
-- ============================================================================

-- IDX-06: tax_no UNIQUE (filtered WHERE manuel)
CREATE UNIQUE NONCLUSTERED INDEX IX_CUSTOMER_TAXNO
    ON app.CUSTOMER (tax_no);
GO

-- IDX-01 + IDX-09 (filtered WHERE - IDX-08 manuel)
CREATE NONCLUSTERED INDEX IX_CUSTOMER_EMAIL
    ON app.CUSTOMER (email)
    INCLUDE (full_name);
GO

-- IDX-04
CREATE NONCLUSTERED INDEX IX_ORDERS_CUSTOMER
    ON app.ORDERS (customer_id, order_date);
GO

-- IDX-10
CREATE NONCLUSTERED INDEX IX_ORDERS_DATE_DESC
    ON app.ORDERS (order_date DESC);
GO

-- IDX-03 rename
CREATE NONCLUSTERED INDEX IX_ORDER_ITEM_PRODUCT
    ON ops.ORDER_ITEM (product_id);
GO

-- IDX-05: kolon sirasi v1 tersi
CREATE NONCLUSTERED INDEX IX_TX_LOG
    ON app.TRANSACTION_LOG (customer_id, txn_date);
GO

-- ============================================================================
-- 14. TRIGGER'LAR
-- ============================================================================

-- TRG-02: genis body
CREATE TRIGGER app.TR_ORDERS_TIMESTAMP
ON app.ORDERS
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE o
    SET last_updated = SYSUTCDATETIME()
    FROM app.ORDERS o
    INNER JOIN inserted i ON o.order_id = i.order_id;

    IF UPDATE(order_status)
    BEGIN
        INSERT INTO app.TRANSACTION_LOG (txn_id, customer_id, txn_date, txn_amount, txn_type)
        SELECT
            NEXT VALUE FOR app.SEQ_TRANSACTION_LOG,
            i.customer_id, SYSUTCDATETIME(), i.order_amount, 'STATUS_CHG'
        FROM inserted i;
    END
END;
GO

-- TRG-01
CREATE TRIGGER app.TR_CUSTOMER_AUDIT
ON app.CUSTOMER
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE(email) OR UPDATE(status)
    BEGIN
        PRINT 'Customer audit: critical field changed';
    END
END;
GO

-- ============================================================================
-- 15. VIEW'LER
-- ============================================================================

-- VW-03
CREATE VIEW app.VW_CUSTOMER_SUMMARY AS
SELECT c.customer_id, c.full_name, c.email, c.email_verified
FROM app.CUSTOMER c;
GO

-- VW-04 (VW-05 indexed/SCHEMABINDING manuel)
CREATE VIEW app.VW_ORDER_DETAIL AS
SELECT o.order_id, o.customer_id, o.order_date, o.order_amount, o.order_status
FROM app.ORDERS o
WHERE o.order_status IN ('OPEN','PENDING');
GO

-- VW-01
CREATE VIEW app.VW_TRANSACTION_DAILY AS
SELECT
    CAST(txn_date AS DATE) AS txn_day,
    customer_id, txn_type,
    COUNT(*)               AS txn_count,
    SUM(txn_amount)        AS total_amount
FROM app.TRANSACTION_LOG
GROUP BY CAST(txn_date AS DATE), customer_id, txn_type;
GO

-- ============================================================================
-- MANUEL ADIMLAR (Erwin parser sinirlamalari nedeniyle SQL'e dahil edilmedi)
-- ============================================================================
-- - COL-12: ALTER TABLE app.CUSTOMER ALTER COLUMN full_name VARCHAR(150)
--           COLLATE Turkish_CI_AS NOT NULL;
-- - COL-14: ALTER TABLE ops.ORDER_ITEM ADD line_total AS
--           (quantity * unit_price) PERSISTED;
-- - COL-15, TBL-05: sp_addextendedproperty ile MS_Description ekle
-- - IDX-07: PK_ORDERS clustered/nonclustered yeniden olustur
-- - IDX-08: filtered WHERE clause: WHERE status='ACTIVE' / WHERE tax_no IS NOT NULL
-- - PART-01, PART-02: partition function/scheme + ON PS_xxx
-- - STO-01: filegroup (FG_DATA)
-- - STO-02: REBUILD WITH (DATA_COMPRESSION = PAGE)
-- - VW-05: SCHEMABINDING + CREATE UNIQUE CLUSTERED INDEX on view

-- BITTI
