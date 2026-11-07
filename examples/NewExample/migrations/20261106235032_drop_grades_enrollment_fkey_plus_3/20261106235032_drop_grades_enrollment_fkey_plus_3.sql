ALTER TABLE "grades" DROP CONSTRAINT "grades_enrollment_fkey"

--> statement-breakpoint

ALTER TABLE "students" DROP COLUMN "created_at"

--> statement-breakpoint

ALTER TABLE "course_offerings" DROP COLUMN "capacity"

--> statement-breakpoint

DROP TABLE "grades"
