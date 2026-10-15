-- The application connects as a role that is not the table owner and not a superuser,
-- so row-level security always applies to it.
CREATE ROLE tallyard_app LOGIN PASSWORD 'tallyard_app';
GRANT CONNECT ON DATABASE tallyard TO tallyard_app;
GRANT USAGE ON SCHEMA public TO tallyard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tallyard IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tallyard_app;
ALTER DEFAULT PRIVILEGES FOR ROLE tallyard IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO tallyard_app;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
