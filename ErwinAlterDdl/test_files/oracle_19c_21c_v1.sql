-- ============================================================================
-- Oracle 19c & 21c - v1 BASELINE (Erwin DM uyumlu)
-- ============================================================================
-- ERWIN UYUM KURALLARI:
-- 1. CONSTRAINT <n> ... syntax'i YOK
-- 2. PK: tablo sonunda isimsiz "PRIMARY KEY (col)"
-- 3. UNIQUE/CHECK: ayri "ALTER TABLE ... ADD" statement'lari ile
-- 4. FK: ayri "ALTER TABLE ... ADD FOREIGN KEY"
-- 5. DEFAULT: kolon-inline
-- 6. TIMESTAMP(0) yerine TIMESTAMP
-- ============================================================================

-- Onkosul (production'da):
--   CREATE USER APP   IDENTIFIED BY ... DEFAULT TABLESPACE USERS;
--   CREATE USER SALES IDENTIFIED BY ... DEFAULT TABLESPACE USERS;

-- ----------------------------------------------------------------------------
-- Sequences
-- ----------------------------------------------------------------------------
CREATE SEQUENCE APP.SEQ_ORDER_NUMBER
    START WITH 1
    INCREMENT BY 1
    MINVALUE 1
    NOCACHE
    NOCYCLE;

-- ----------------------------------------------------------------------------
-- PRODUCT
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PRODUCT (
    product_id     NUMBER(10)       NOT NULL,
    product_code   VARCHAR2(30)     NOT NULL,
    product_name   VARCHAR2(200)    NOT NULL,
    list_price     NUMBER(12,2)     NOT NULL,
    is_active      NUMBER(1)        NOT NULL,
    created_at     TIMESTAMP        NOT NULL,
    PRIMARY KEY (product_id)
);

CREATE INDEX APP.IX_PRODUCT_NAME ON APP.PRODUCT (product_name);

-- ----------------------------------------------------------------------------
-- PROMOTION
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PROMOTION (
    promotion_id    NUMBER(10)      NOT NULL,
    code            VARCHAR2(30)    NOT NULL,
    description     VARCHAR2(500),
    discount_pct    NUMBER(5,2)     NOT NULL,
    start_date      DATE            NOT NULL,
    end_date        DATE            NOT NULL,
    PRIMARY KEY (promotion_id)
);

-- ----------------------------------------------------------------------------
-- CUSTOMER
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CUSTOMER (
    customer_id    NUMBER(10)       NOT NULL,
    full_name      VARCHAR2(150)    NOT NULL,
    email          VARCHAR2(200)    NOT NULL,
    tax_no         VARCHAR2(20),
    address        VARCHAR2(100),
    mobile_phone   VARCHAR2(20),
    fax_number     VARCHAR2(20),
    status         VARCHAR2(10)     NOT NULL,
    created_at     TIMESTAMP        NOT NULL,
    PRIMARY KEY (customer_id)
);

CREATE INDEX APP.IX_CUSTOMER_TAXNO ON APP.CUSTOMER (tax_no);

-- ----------------------------------------------------------------------------
-- ORDERS
-- ----------------------------------------------------------------------------
CREATE TABLE APP.ORDERS (
    order_id       NUMBER(10)       NOT NULL,
    customer_id    NUMBER(10),
    promotion_id   NUMBER(10),
    order_date     TIMESTAMP        NOT NULL,
    order_amount   NUMBER(10)       NOT NULL,
    order_status   VARCHAR2(20)     DEFAULT 'NEW' NOT NULL,
    last_updated   TIMESTAMP,
    PRIMARY KEY (order_id)
);

CREATE INDEX APP.IX_ORDERS_DATE       ON APP.ORDERS (order_date);
CREATE INDEX APP.IX_ORDERS_CUSTOMER   ON APP.ORDERS (customer_id);
CREATE INDEX APP.IX_ORDERS_PROMOTION  ON APP.ORDERS (promotion_id);

CREATE OR REPLACE TRIGGER APP.TR_ORDERS_TIMESTAMP
BEFORE INSERT OR UPDATE ON APP.ORDERS
FOR EACH ROW
BEGIN
    :NEW.last_updated := SYSTIMESTAMP;
END;
/

-- ----------------------------------------------------------------------------
-- ORDER_ITEM (v1 SALES schema)
-- ----------------------------------------------------------------------------
CREATE TABLE SALES.ORDER_ITEM (
    order_id       NUMBER(10)       NOT NULL,
    line_no        NUMBER(10)       NOT NULL,
    product_id     NUMBER(10)       NOT NULL,
    quantity       NUMBER(10)       DEFAULT 1 NOT NULL,
    unit_price     NUMBER(18,4)     NOT NULL,
    discount_pct   NUMBER(5,2)      NOT NULL,
    PRIMARY KEY (order_id)
);

CREATE INDEX SALES.IX_OI_PROD ON SALES.ORDER_ITEM (product_id);

-- ----------------------------------------------------------------------------
-- TRANSACTION_LOG
-- ----------------------------------------------------------------------------
CREATE TABLE APP.TRANSACTION_LOG (
    txn_id         NUMBER(19)       NOT NULL,
    customer_id    NUMBER(10),
    txn_date       TIMESTAMP        NOT NULL,
    txn_amount     NUMBER(18,2)     NOT NULL,
    txn_type       VARCHAR2(10)     NOT NULL
);

CREATE INDEX APP.IX_TX_LOG ON APP.TRANSACTION_LOG (txn_date, customer_id);

-- ----------------------------------------------------------------------------
-- PRODUCT_ARCHIVE (v2'de drop)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.PRODUCT_ARCHIVE (
    product_id     NUMBER(10)       NOT NULL,
    product_name   VARCHAR2(200)    NOT NULL,
    archived_at    TIMESTAMP        NOT NULL,
    PRIMARY KEY (product_id)
);

-- ----------------------------------------------------------------------------
-- CUSTOMER_BACKUP (v2'de CUSTOMER_HISTORY rename)
-- ----------------------------------------------------------------------------
CREATE TABLE APP.CUSTOMER_BACKUP (
    customer_id    NUMBER(10)       NOT NULL,
    snapshot_date  DATE             NOT NULL,
    snapshot_data  CLOB,
    PRIMARY KEY (customer_id, snapshot_date)
);

-- ----------------------------------------------------------------------------
-- UNIQUE CONSTRAINTS (ayri ALTER TABLE)
-- ----------------------------------------------------------------------------
ALTER TABLE APP.PRODUCT  ADD UNIQUE (product_code);
ALTER TABLE APP.CUSTOMER ADD UNIQUE (tax_no);

-- ----------------------------------------------------------------------------
-- CHECK CONSTRAINTS (ayri ALTER TABLE)
-- ----------------------------------------------------------------------------
ALTER TABLE APP.PROMOTION ADD CHECK (end_date >= start_date);
ALTER TABLE APP.ORDERS    ADD CHECK (order_amount > 0);

-- ----------------------------------------------------------------------------
-- FOREIGN KEYS (ayri ALTER TABLE)
-- ----------------------------------------------------------------------------
ALTER TABLE APP.ORDERS
    ADD FOREIGN KEY (customer_id) REFERENCES APP.CUSTOMER (customer_id);

ALTER TABLE APP.ORDERS
    ADD FOREIGN KEY (promotion_id) REFERENCES APP.PROMOTION (promotion_id);

ALTER TABLE SALES.ORDER_ITEM
    ADD FOREIGN KEY (order_id) REFERENCES APP.ORDERS (order_id);

ALTER TABLE SALES.ORDER_ITEM
    ADD FOREIGN KEY (product_id) REFERENCES APP.PRODUCT (product_id);

-- ----------------------------------------------------------------------------
-- VIEWS
-- ----------------------------------------------------------------------------
CREATE OR REPLACE VIEW APP.VW_CUSTOMER_SUMMARY AS
SELECT
    c.customer_id,
    c.full_name,
    c.email
FROM APP.CUSTOMER c;

CREATE OR REPLACE VIEW APP.VW_ORDER_DETAIL AS
SELECT
    o.order_id,
    o.customer_id,
    o.order_date,
    o.order_amount,
    o.order_status
FROM APP.ORDERS o
WHERE o.order_status = 'OPEN';

CREATE OR REPLACE VIEW APP.VW_LEGACY_CUSTOMER AS
SELECT
    customer_id,
    full_name,
    fax_number
FROM APP.CUSTOMER;
