ALTER TABLE "professors" ALTER COLUMN "email" TYPE varchar(320)

--> statement-breakpoint

ALTER TABLE "students" ALTER COLUMN "enrollment_year" DROP NOT NULL

--> statement-breakpoint

ALTER TABLE "students" ADD COLUMN "phone" varchar(30)

--> statement-breakpoint

ALTER TABLE "courses" ALTER COLUMN "credits" SET DEFAULT 5

--> statement-breakpoint

ALTER TABLE "professors" ADD CONSTRAINT "professors_department_id_email_key" UNIQUE ("department_id", "email")

--> statement-breakpoint

CREATE INDEX "grades_graded_at_idx" ON "grades" ("graded_at")
