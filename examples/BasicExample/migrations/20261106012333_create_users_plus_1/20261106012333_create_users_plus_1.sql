CREATE TABLE "users" (
    "id" integer NOT NULL,
    "name" text NOT NULL,
    "age" integer
)

--> statement-breakpoint

CREATE TABLE "orders" (
    "id" integer NOT NULL,
    "user_id" integer NOT NULL,
    "amount" integer NOT NULL,
    "note" text
)
